using Rodaje.Analysis.Video;
using Rodaje.App.Services;
using Rodaje.App.Tests.Effects;
using Rodaje.App.Tests.Support;
using Rodaje.App.Views.Analysis;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using C = Rodaje.Core.Model.BuiltInEffects.ColorParameters;
using S = Rodaje.Core.Model.BuiltInEffects;

namespace Rodaje.App.Tests.Analysis;

public class VideoAnalysisTests
{
    private static Clip First(EditorSession session) => session.MainTrack!.Clips[0];

    [Fact]
    public void Estabilizar_PoneElEfectoAlPrincipioEnUnPaso_YSeDeshace() => UiThread.Run(async () =>
    {
        EditorSession session = await EffectCommandsTests.TwoClips();
        string clipId = First(session).Id;
        First(session).Effects.Add(new EffectInstance { Id = "t", Type = S.Transform });
        int steps = session.History.Count;

        Assert.True(session.Execute(VideoAnalysisEdits.Stabilize(session.SequenceId, clipId, "abc123", 0.8, autoCrop: false)));

        EffectInstance stabilize = First(session).Effects[0];
        Assert.Equal(S.Stabilize, stabilize.Type);
        Assert.Equal(0.8, stabilize.Parameters[S.StabilizeParameters.Intensity].Value);
        Assert.Equal("off", stabilize.Options[S.StabilizeOptions.Crop]);
        Assert.Equal("abc123", stabilize.Options[S.StabilizeOptions.Analysis]);
        Assert.Equal(steps + 1, session.History.Count);

        // Otra vez sobre el mismo efecto: no se duplica.
        Assert.True(session.Execute(VideoAnalysisEdits.Stabilize(session.SequenceId, clipId, "def456", 1.5, autoCrop: true)));
        Assert.Single(First(session).Effects, e => e.Type == S.Stabilize);
        Assert.Equal(1, First(session).Effects[0].Parameters[S.StabilizeParameters.Intensity].Value);
        Assert.Equal("auto", First(session).Effects[0].Options[S.StabilizeOptions.Crop]);

        session.Undo();
        session.Undo();
        Assert.DoesNotContain(First(session).Effects, e => e.Type == S.Stabilize);
    });

    [Fact]
    public void ColorAutomatico_AjustaElColorExistenteEnUnPaso_YQuitaSusKeyframes() => UiThread.Run(async () =>
    {
        EditorSession session = await EffectCommandsTests.TwoClips();
        string clipId = First(session).Id;
        var color = new EffectInstance { Id = "c", Type = S.Color };
        color.Parameters[C.Temperature] = new Parameter { Keyframes = [new Keyframe(Time.Zero, 0.2), new Keyframe(Time.FromSeconds(1), 0.4)] };
        color.Parameters[C.Saturation] = new Parameter { Value = 0.3 };
        First(session).Effects.Add(color);
        int steps = session.History.Count;

        Assert.True(session.Execute(VideoAnalysisEdits.Color(session.SequenceId, clipId, new ColorSettings(0.25, 0.4, -0.3, 0.1), levels: true)));

        EffectInstance after = Assert.Single(First(session).Effects);
        Assert.Equal(-0.3, after.Parameters[C.Temperature].Value);
        Assert.Empty(after.Parameters[C.Temperature].Keyframes);
        Assert.Equal(0.1, after.Parameters[C.Tint].Value);
        Assert.Equal(0.25, after.Parameters[C.Exposure].Value);
        Assert.Equal(0.4, after.Parameters[C.Contrast].Value);
        Assert.Equal(0.3, after.Parameters[C.Saturation].Value);
        Assert.Equal(steps + 1, session.History.Count);
        Assert.Equal("Color automático", session.UndoDescription);

        session.Undo();
        Assert.Equal(2, First(session).Effects[0].Parameters[C.Temperature].Keyframes.Count);
    });

    [Fact]
    public void Balance_CreaElColorSiFalta_YNoTocaElContraste() => UiThread.Run(async () =>
    {
        EditorSession session = await EffectCommandsTests.TwoClips();
        Assert.True(session.Execute(VideoAnalysisEdits.Color(session.SequenceId, First(session).Id, new ColorSettings(0.05, 0.4, 0.5, -0.2), levels: false)));

        EffectInstance color = Assert.Single(First(session).Effects);
        Assert.Equal(S.Color, color.Type);
        Assert.Equal(0.5, color.Parameters[C.Temperature].Value);
        Assert.False(color.Parameters.ContainsKey(C.Contrast));
        Assert.Equal("Balance de blancos automático", session.UndoDescription);
    });

    [Fact]
    public void LaCacheGuardaElAnalisis_YDaLasCorreccionesConElRecorte()
    {
        using var folder = new TempDirectory();
        var store = new StabilizationStore(folder.Path);
        Similarity[] path = [.. Enumerable.Range(0, 60).Select(k => Similarity.FromParts(0.002 * Math.Sin(k), 1, 0.01 * Math.Sin(k * 1.7), 0.01 * Math.Cos(k * 2.3)))];
        FrameMotion[] motions = [.. path.Select((p, k) => k == 0 ? FrameMotion.None : new FrameMotion(p.After(path[k - 1].Inverse()), 200, 180))];
        string key = StabilizationStore.KeyFor("huella", 30, motions.Length);
        Assert.Null(store.Find(key, 0.5, true));

        store.Save(key, new MotionTrack(1920, 1080, Rational.Fps30, 30, motions));

        var fresh = new StabilizationStore(folder.Path);
        Playback.Composition.StabilizationTrack? track = fresh.Find(key, 0.5, true);
        Assert.NotNull(track);
        Assert.Equal(30, track.FirstFrame);
        Assert.Equal(60, track.Count);
        Assert.True(track.At(45).Scale > 1, "El recorte automático acerca la imagen.");
        Assert.Same(track, fresh.Find(key, 0.5, true));
        Assert.Equal(1, fresh.Find(key, 0.5, false)!.At(0).Scale, 3);
        Assert.Null(fresh.Find("../fuera", 0.5, true));
    }
}
