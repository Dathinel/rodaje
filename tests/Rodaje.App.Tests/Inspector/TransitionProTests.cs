using Rodaje.App.Services;
using Rodaje.App.Services.Inspector;
using Rodaje.App.Services.Settings;
using Rodaje.App.Tests.Effects;
using Rodaje.App.Tests.Support;
using Rodaje.App.Views.Timeline;
using Rodaje.Core.Commands;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Tests.Inspector;

/// <summary>Transición por defecto (Ctrl+T), alineación, curva, color y duración de varias desde el inspector y la línea de tiempo.</summary>
public class TransitionProTests
{
    private static Time S(double seconds) => Time.FromSeconds(seconds);

    [Fact]
    public void CtrlT_EnElCorteMasCercanoConLaDuracionAlineacionYCurvaPorDefecto() => UiThread.Run(async () =>
    {
        EditorSession session = await EffectCommandsTests.TwoClips();
        session.Select([]);
        var preferences = new TransitionPreferences
        {
            Reference = BuiltInTransitions.Slide, Seconds = 0.8, Alignment = TransitionAlignment.After, Curve = Interpolation.EaseOut,
        };
        IReadOnlyList<string> cuts = DefaultTransition.Cuts(session.Sequence, [], S(9), session.MainTrack);
        Assert.Equal([session.MainTrack!.Clips[0].Id], cuts);

        int steps = session.History.Count;
        SequenceEditCommand command = DefaultTransition.Build(session.SequenceId, cuts, preferences);
        Assert.True(session.Execute(command));
        TransitionInstance transition = Assert.Single(session.MainTrack.Transitions);
        Assert.Equal((BuiltInTransitions.Slide, S(0.8), TransitionAlignment.After, (Interpolation?)Interpolation.EaseOut),
            (transition.Type, transition.Duration, transition.Alignment, transition.Curve));
        Assert.Equal(steps + 1, session.History.Count);

        // Otra vez en el mismo corte: cambia el tipo y conserva la duración.
        preferences.Reference = BuiltInTransitions.Glitch;
        preferences.Seconds = 2;
        Assert.True(session.Execute(DefaultTransition.Build(session.SequenceId, cuts, preferences)));
        Assert.Equal((BuiltInTransitions.Glitch, S(0.8)), (session.MainTrack.Transitions[0].Type, session.MainTrack.Transitions[0].Duration));
        session.Undo();
        session.Undo();
        Assert.Empty(session.MainTrack.Transitions);
    });

    [Fact]
    public void CtrlT_ConClipsSeleccionadosVaEnSusCortesYEnAudioPoneElFundidoDeAudio() => UiThread.Run(async () =>
    {
        EditorSession session = await EffectCommandsTests.TwoClips();
        Track audio = session.Sequence.AudioTracks[0];
        Clip lastVideo = session.MainTrack!.Clips[1];
        IReadOnlyList<string> cuts = DefaultTransition.Cuts(session.Sequence, [lastVideo.Id, audio.Clips[0].Id], S(0), session.MainTrack);
        Assert.Equal([session.MainTrack.Clips[0].Id, audio.Clips[0].Id], cuts);

        var preferences = new TransitionPreferences { Reference = "rodaje-transiciones@1.2.0/giro-rapido", Seconds = 30 };
        Assert.True(session.Execute(DefaultTransition.Build(session.SequenceId, cuts, preferences)));

        TransitionInstance video = Assert.Single(session.MainTrack.Transitions);
        Assert.Equal(BuiltInTransitions.Preset, video.Type);
        Assert.Equal("rodaje-transiciones@1.2.0/giro-rapido", video.Options[BuiltInTransitions.PresetOptions.Preset]);
        Assert.Equal(S(10), video.Duration);
        Assert.Equal(BuiltInTransitions.AudioPower, Assert.Single(audio.Transitions).Type);
    });

    [Fact]
    public void CtrlT_SinCortesAvisaYNoCambiaNada() => UiThread.Run(async () =>
    {
        EditorSession session = await EffectCommandsTests.TwoClips();
        Assert.Empty(DefaultTransition.Cuts(session.Sequence, ["no-existe"], S(0), session.MainTrack));
    });

    [Fact]
    public void UsarComoPorDefecto_GuardaTipoDuracionAlineacionYCurva()
    {
        var preferences = new TransitionPreferences();
        var transition = new TransitionInstance
        {
            Type = BuiltInTransitions.Preset, Duration = S(1.2), Alignment = TransitionAlignment.Before, Curve = Interpolation.Bezier,
            Ease = new BezierEase(0.1, 0.9, 0.2, 1), Options = { [BuiltInTransitions.PresetOptions.Preset] = "p@1.0.0/x" },
        };
        DefaultTransition.Remember(preferences, transition);
        Assert.Equal(("p@1.0.0/x", 1.2, TransitionAlignment.Before), (preferences.Reference, preferences.Seconds, preferences.Alignment));
        Assert.Equal(new BezierEase(0.1, 0.9, 0.2, 1), preferences.Ease);

        DefaultTransition.Remember(preferences, new TransitionInstance { Type = BuiltInTransitions.AudioGain, Duration = S(0.3) });
        Assert.Equal(("p@1.0.0/x", BuiltInTransitions.AudioGain), (preferences.Reference, preferences.AudioReference));

        var broken = new TransitionPreferences { Reference = BuiltInTransitions.AudioGain, AudioReference = "rodaje.wipe", Seconds = double.NaN, Alignment = (TransitionAlignment)9 };
        broken.Normalize();
        Assert.Equal((BuiltInTransitions.Crossfade, BuiltInTransitions.AudioPower, 0.5, TransitionAlignment.Center),
            (broken.Reference, broken.AudioReference, broken.Seconds, broken.Alignment));
    }

    [Fact]
    public void Inspector_AlineacionCurvaColorYDuracionDeVarias() => UiThread.Run(async () =>
    {
        EditorSession session = await EffectCommandsTests.TwoClips();
        var editor = new TransitionEditor(session);
        var add = Edits.AddTransition(session.SequenceId, session.MainTrack!.Clips[0].Id, BuiltInTransitions.Wipe, S(1));
        Assert.True(session.Execute(add));
        string id = add.CreatedIds[0];
        var audio = Edits.AddTransition(session.SequenceId, session.Sequence.AudioTracks[0].Clips[0].Id, BuiltInTransitions.AudioPower, S(0.25));
        Assert.True(session.Execute(audio));

        Assert.True(editor.SetAlignment(id, TransitionAlignment.Before));
        Assert.False(editor.SetAlignment(id, TransitionAlignment.Before));
        Assert.Equal(TransitionAlignment.Before, editor.Find(id)!.Transition.Alignment);

        int steps = session.History.Count;
        Assert.True(editor.SetCurve(id, Interpolation.Bezier, new BezierEase(0.2, 0.1, 0.3, 1)));
        Assert.True(editor.SetCurve(id, Interpolation.Bezier, new BezierEase(0.25, 0.1, 0.3, 1)));
        Assert.Equal(steps + 1, session.History.Count);
        Assert.True(editor.SetCurve(id, Interpolation.Linear));
        Assert.Null(editor.Find(id)!.Transition.Curve);

        session.BreakMerge();
        steps = session.History.Count;
        Assert.True(editor.SetColor(id, BuiltInTransitions.CommonParameters.BorderRed, BuiltInTransitions.CommonParameters.BorderGreen,
            BuiltInTransitions.CommonParameters.BorderBlue, 1, 0.5, 0));
        Assert.True(editor.SetColor(id, BuiltInTransitions.CommonParameters.BorderRed, BuiltInTransitions.CommonParameters.BorderGreen,
            BuiltInTransitions.CommonParameters.BorderBlue, 1, 0.25, 0));
        Assert.Equal(steps + 1, session.History.Count);
        Assert.Equal(0.25, editor.Find(id)!.Transition.Parameters[BuiltInTransitions.CommonParameters.BorderGreen]);

        Assert.Equal(1, editor.ApplyDuration(id, TransitionEditor.DurationScope.Sequence));
        Assert.Equal(S(1), session.Sequence.AudioTracks[0].Transitions[0].Duration);
        Assert.Equal(0, editor.ApplyDuration(id, TransitionEditor.DurationScope.Track));
    });

    [Fact]
    public void LineaDeTiempo_AlineadaSeArrastraSoloDesdeLaManijaDeAfuera() => UiThread.Run(async () =>
    {
        EditorSession session = await EffectCommandsTests.TwoClips();
        Track main = session.MainTrack!;
        Assert.True(session.Execute(Edits.AddTransition(session.SequenceId, main.Clips[0].Id, BuiltInTransitions.Crossfade, S(1))));
        TransitionInstance fade = main.Transitions[0];
        Assert.True(session.Execute(Edits.SetTransitionAlignment(session.SequenceId, fade.Id, TransitionAlignment.Before)));
        TrackLayout layout = TrackLayout.Build(session.Sequence, main);
        TrackRow row = layout.Find(main.Id)!;
        var vp = new TimelineViewport { PixelsPerSecond = 100, Width = 3000 };
        (double top, double bottom) = TransitionPlanning.Band(row);
        double y = (top + bottom) / 2;

        Assert.Equal((900.0, 1000.0), TransitionPlanning.Span(vp, S(10), fade));
        Assert.Equal(TransitionHitKind.StartHandle, TransitionPlanning.Hit(layout, vp, 901, y, null).Kind);
        Assert.Equal(TransitionHitKind.Body, TransitionPlanning.Hit(layout, vp, 999, y, null).Kind);

        Rational rate = session.Sequence.FrameRate;
        Assert.Equal(S(1.5), TransitionPlanning.DurationForHandle(main, fade, S(8.5), rate));
        Assert.Equal(S(10), TransitionPlanning.DurationForHandle(main, fade, S(-5), rate));
    });

    [Fact]
    public void Miniaturas_ImagenesDeMuestraDistintasYFirmaQueCambiaConLoQueSeVe()
    {
        (byte[] a, byte[] b) = TransitionSampleImages.Create(64, 36);
        Assert.Equal(64 * 36 * 4, a.Length);
        Assert.NotEqual(a, b);
        Assert.Equal(a, TransitionSampleImages.Create(64, 36).Outgoing);
        Assert.All(Enumerable.Range(0, 64 * 36), i => Assert.Equal(255, a[(i * 4) + 3]));

        var transition = new TransitionInstance { Type = BuiltInTransitions.Wipe, Duration = S(1) };
        string plain = TransitionPreviews.Signature(transition, 64, 36);
        transition.Parameters[BuiltInTransitions.WipeParameters.Angle] = 90;
        string angled = TransitionPreviews.Signature(transition, 64, 36);
        transition.Curve = Interpolation.EaseIn;
        Assert.Equal(3, new[] { plain, angled, TransitionPreviews.Signature(transition, 64, 36) }.Distinct().Count());
        Assert.NotEqual(angled, TransitionPreviews.Signature(transition, 128, 72));
    }
}
