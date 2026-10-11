using Rodaje.App.Services;
using Rodaje.App.Tests.Support;
using Rodaje.App.Views.Timeline;
using Rodaje.Core.Commands;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Tests.TimelineLogic;

public class TransitionAndEnvelopeTests
{
    private static Time S(double seconds) => Time.FromSeconds(seconds);

    /// <summary>Dos clips de 10 s seguidos en la principal (corte a los 10 s) con su audio enlazado.</summary>
    private static async Task<EditorSession> TwoClips()
    {
        var prober = new FakeProber().Video("a.mp4", 10).Video("b.mp4", 10);
        var session = new EditorSession(new FakePlayback(), prober, new FakeDialogs(), new SequentialIdGenerator());
        await session.OpenFilesAsync([Path.GetFullPath("a.mp4"), Path.GetFullPath("b.mp4")]);
        return session;
    }

    private static TimelineViewport Viewport() => new() { PixelsPerSecond = 100, Width = 3000 };

    [Fact]
    public void ElSimboloCreaUnFundidoQueSeSeleccionaYSeBorraConSupr() => UiThread.Run(async () =>
    {
        EditorSession session = await TwoClips();
        Track main = session.MainTrack!;
        Assert.Equal([(main.Clips[0], S(10))], TransitionPlanning.OpenCuts(main));

        TrackLayout layout = TrackLayout.Build(session.Sequence, main);
        TrackRow row = layout.Find(main.Id)!;
        TimelineViewport vp = Viewport();
        double centerY = row.Top + (row.Height / 2);
        Assert.Equal(TransitionHitKind.None, TransitionPlanning.Hit(layout, vp, 1000, centerY, null).Kind);
        TransitionHit symbol = TransitionPlanning.Hit(layout, vp, 1000, centerY, main);
        Assert.Equal(TransitionHitKind.AutoCrossfade, symbol.Kind);

        SequenceEditCommand add = TransitionPlanning.BuildAutoCrossfade(session.SequenceId, symbol.From!.Id);
        Assert.True(session.Execute(add));
        TransitionInstance fade = Assert.Single(main.Transitions);
        Assert.Equal(BuiltInTransitions.Crossfade, fade.Type);
        Assert.Equal(EffectRegistry.DefaultTransitionDuration, fade.Duration);
        Assert.Empty(TransitionPlanning.OpenCuts(main));

        session.Select([main.Clips[0].Id]);
        session.SelectTransition(fade.Id);
        Assert.Empty(session.SelectedClipIds);
        Assert.Equal(fade.Id, session.SelectedTransitionId);
        Assert.True(session.DeleteSelection());
        Assert.Empty(main.Transitions);
        Assert.Null(session.SelectedTransitionId);
        Assert.Equal(2, main.Clips.Count);

        session.Undo();
        Assert.Single(main.Transitions);
        session.SelectTransition(main.Transitions[0].Id);
        session.Select([main.Clips[1].Id]);
        Assert.Null(session.SelectedTransitionId);
        session.SelectTransition(main.Transitions[0].Id);
        session.Undo();
        Assert.Null(session.SelectedTransitionId);
    });

    [Fact]
    public void ElBloqueTieneManijasYArrastrarlasCambiaLaDuracionEnUnPaso() => UiThread.Run(async () =>
    {
        EditorSession session = await TwoClips();
        Track main = session.MainTrack!;
        Assert.True(session.Execute(TransitionPlanning.BuildAutoCrossfade(session.SequenceId, main.Clips[0].Id)));
        TransitionInstance fade = main.Transitions[0];
        TrackLayout layout = TrackLayout.Build(session.Sequence, main);
        TrackRow row = layout.Find(main.Id)!;
        TimelineViewport vp = Viewport();
        (double top, double bottom) = TransitionPlanning.Band(row);
        double y = (top + bottom) / 2;

        Assert.Equal((975.0, 1025.0), TransitionPlanning.Span(vp, S(10), fade));
        Assert.Equal(TransitionHitKind.StartHandle, TransitionPlanning.Hit(layout, vp, 976, y, null).Kind);
        Assert.Equal(TransitionHitKind.EndHandle, TransitionPlanning.Hit(layout, vp, 1024, y, null).Kind);
        Assert.Equal(TransitionHitKind.Body, TransitionPlanning.Hit(layout, vp, 1000, y, null).Kind);
        Assert.Equal(TransitionHitKind.None, TransitionPlanning.Hit(layout, vp, 1000, row.Top + 1, null).Kind);

        Rational rate = session.Sequence.FrameRate;
        Time frame = Time.FromFrames(1, rate);
        Assert.Equal(frame + frame, TransitionPlanning.DurationForHandle(main, fade, S(10), rate));
        Assert.Equal(S(10) + S(10), TransitionPlanning.DurationForHandle(main, fade, S(40), rate));
        Time oneSecond = TransitionPlanning.DurationForHandle(main, fade, S(9.5), rate);
        Assert.Equal(TransitionPlanning.DurationForHandle(main, fade, S(10.5), rate), oneSecond);
        Assert.InRange(oneSecond.ToSeconds(), 0.95, 1.05);

        int steps = session.History.Count;
        session.BeginGesture("Cambiar duración de la transición");
        foreach (double x in new[] { 9.8, 9.6, 9.5 })
        {
            session.TryExecute(Edits.SetTransitionDuration(session.SequenceId, fade.Id, TransitionPlanning.DurationForHandle(main, fade, S(x), rate)));
        }

        session.EndGesture();
        Assert.Equal(steps + 1, session.History.Count);
        Assert.Equal(oneSecond, main.Transitions[0].Duration);

        // Con poco zoom el bloque se ensancha alrededor del corte para poder agarrarlo.
        var far = new TimelineViewport { PixelsPerSecond = 2, Width = 3000 };
        (double left, double right) = TransitionPlanning.Span(far, S(10), main.Transitions[0]);
        Assert.Equal(TransitionPlanning.MinBlockWidth, right - left, 6);
    });

    [Fact]
    public void SoltarUnaTransicionLaCreaOLeCambiaElTipo() => UiThread.Run(async () =>
    {
        EditorSession session = await TwoClips();
        Track main = session.MainTrack!;
        Assert.Null(TransitionPlanning.NearestCut(main, S(9), S(0.5)));
        (Clip from, Time cut) = TransitionPlanning.NearestCut(main, S(9.8), S(0.5))!.Value;
        Assert.Equal(S(10), cut);

        Assert.True(session.Execute(TransitionPlanning.BuildDrop(session.SequenceId, from.Id, BuiltInTransitions.Wipe)));
        Assert.True(session.Execute(Edits.SetTransitionDuration(session.SequenceId, main.Transitions[0].Id, S(1))));
        SequenceEditCommand again = TransitionPlanning.BuildDrop(session.SequenceId, from.Id, "rodaje-esenciales@1.0.0/barrido-suave");
        Assert.True(session.Execute(again));
        TransitionInstance preset = Assert.Single(main.Transitions);
        Assert.Equal(again.CreatedIds[0], preset.Id);
        Assert.Equal(BuiltInTransitions.Preset, preset.Type);
        Assert.Equal("rodaje-esenciales@1.0.0/barrido-suave", preset.Options[BuiltInTransitions.PresetOptions.Preset]);
        Assert.Equal(S(1), preset.Duration);

        Assert.True(TransitionPlanning.IsTransitionReference(BuiltInTransitions.Dip, packageAllowed: false));
        Assert.False(TransitionPlanning.IsTransitionReference(BuiltInTransitions.Preset, packageAllowed: true));
        Assert.False(TransitionPlanning.IsTransitionReference(BuiltInEffects.Color, packageAllowed: true));
        Assert.False(TransitionPlanning.IsTransitionReference("rodaje-esenciales@1.0.0/x", packageAllowed: false));
        Assert.True(TransitionPlanning.IsTransitionReference("rodaje-esenciales@1.0.0/x", packageAllowed: true));
        Assert.False(TransitionPlanning.IsTransitionReference("", packageAllowed: true));
    });

    [Fact]
    public void LaEnvolventePonePuntosEnDbSinPasarASusVecinos() => UiThread.Run(async () =>
    {
        EditorSession session = await TwoClips();
        Track audio = session.Sequence.AudioTracks[0];
        Clip clip = audio.Clips[1];
        TrackRow row = TrackLayout.Build(session.Sequence, session.MainTrack).Find(audio.Id)!;
        Assert.Equal(0, EnvelopeEdits.GainDbAtY(row, EnvelopeEdits.EnvelopeY(row, 0)), 1);
        Assert.Equal(-12, EnvelopeEdits.GainDbAtY(row, EnvelopeEdits.EnvelopeY(row, -12)), 1);

        int steps = session.History.Count;
        session.BeginGesture("Envolvente de volumen");
        Assert.True(session.TryExecute(EnvelopeEdits.SetPoint(session.SequenceId, clip.Id, null, S(12), -6)));
        Assert.True(session.TryExecute(EnvelopeEdits.SetPoint(session.SequenceId, clip.Id, S(12), S(13), -3)));
        session.EndGesture();
        Assert.Equal(steps + 1, session.History.Count);
        Assert.Equal([(S(13), -3.0)], ClipDecorations.GainPoints(audio.Clips[1]));
        Assert.Equal(BuiltInEffects.Audio, Assert.Single(audio.Clips[1].Effects).Type);

        Assert.True(session.Execute(EnvelopeEdits.SetPoint(session.SequenceId, clip.Id, null, S(16), 0)));
        Assert.True(session.Execute(EnvelopeEdits.SetPoint(session.SequenceId, clip.Id, S(13), S(19), 3)));
        List<(Time Time, double GainDb)> points = ClipDecorations.GainPoints(audio.Clips[1]);
        Assert.Equal(2, points.Count);
        Assert.True(points[0].Time < S(16));
        Assert.Equal(3, points[0].GainDb);

        TimelineViewport vp = Viewport();
        Assert.Equal(S(16), EnvelopeEdits.PointAt(audio.Clips[1], row, vp, 1602, EnvelopeEdits.EnvelopeY(row, 0) + 3));
        Assert.Null(EnvelopeEdits.PointAt(audio.Clips[1], row, vp, 1620, EnvelopeEdits.EnvelopeY(row, 0)));

        Assert.True(session.Execute(EnvelopeEdits.SetPoint(session.SequenceId, clip.Id, null, S(40), 99)));
        Assert.Equal((S(20), 24.0), ClipDecorations.GainPoints(audio.Clips[1])[^1]);

        Assert.True(session.Execute(EnvelopeEdits.RemovePoint(session.SequenceId, clip.Id, S(20))));
        Assert.True(session.Execute(EnvelopeEdits.RemovePoint(session.SequenceId, clip.Id, S(16))));
        Assert.True(session.Execute(EnvelopeEdits.RemovePoint(session.SequenceId, clip.Id, points[0].Time)));
        Assert.False(ClipDecorations.HasAnimatedGain(audio.Clips[1]));
        Assert.Equal(3, ClipDecorations.GainDbAt(audio.Clips[1], S(15)));
    });

    [Fact]
    public void ElVolumenDePistaVaEnSusEfectos() => UiThread.Run(async () =>
    {
        EditorSession session = await TwoClips();
        Track audio = session.Sequence.AudioTracks[0];
        Assert.Equal(0, EnvelopeEdits.TrackGainDb(audio, S(3)));

        int steps = session.History.Count;
        session.BeginGesture("Volumen de la pista");
        foreach (double db in new[] { -1.0, -3, -6 })
        {
            Assert.True(session.TryExecute(EnvelopeEdits.SetTrackGain(session.SequenceId, audio.Id, db, S(3))));
        }

        session.EndGesture();
        Assert.Equal(steps + 1, session.History.Count);
        EffectInstance volume = Assert.Single(audio.Effects);
        Assert.Equal(BuiltInEffects.Audio, volume.Type);
        Assert.Equal(-6, EnvelopeEdits.TrackGainDb(audio, S(18)));

        volume.Parameters[BuiltInEffects.AudioParameters.GainDb].Keyframes.Add(new Keyframe(S(0), -6));
        Assert.True(session.Execute(EnvelopeEdits.SetTrackGain(session.SequenceId, audio.Id, 0, S(10))));
        Assert.Equal(-3, EnvelopeEdits.TrackGainDb(session.Sequence.AudioTracks[0], S(5)), 6);

        Assert.False(session.TryExecute(EnvelopeEdits.SetTrackGain(session.SequenceId, session.MainTrack!.Id, 0, S(0))));
    });
}
