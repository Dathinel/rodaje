using Rodaje.App.Services;
using Rodaje.App.Services.Effects;
using Rodaje.App.Services.Inspector;
using Rodaje.App.Tests.Support;
using Rodaje.Core.Commands;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Tests.Inspector;

public sealed class SpeedGraphTests
{
    private static readonly SpeedPoint[] Three = [new(0, 100), new(5000, 300), new(10_000, 100)];

    private static Time S(double value) => Time.FromSeconds(value);

    private static Clip Clip(Time? offset = null, Time? duration = null) => new()
    {
        Id = "clip",
        MediaId = "media",
        Start = S(20),
        SourceIn = S(5),
        SourceOut = S(15),
        Duration = duration ?? S(10),
        Speed = new ClipSpeed { Offset = offset ?? Time.Zero, Curve = new SpeedCurve { Span = S(10), Points = [.. Three] } },
    };

    private static List<Keyframe> Keys(Clip clip) => SpeedGraph.View(clip)!.Value.Effect.Parameters[SpeedGraph.Name].Keyframes;

    [Fact]
    public void LaRampaSeVeComoKeyframesLineales_EnTiempoDeLinea_YVuelveIgual()
    {
        Clip clip = Clip();
        (Clip proxy, EffectInstance effect) = SpeedGraph.View(clip)!.Value;
        List<Keyframe> keys = effect.Parameters[SpeedGraph.Name].Keyframes;
        Assert.Equal(clip.Id, proxy.Id);
        Assert.Equal(clip.End, proxy.End);
        Assert.Equal([S(20), S(25), S(30)], keys.Select(k => EffectCommands.TimelineOfKeyframe(proxy, k.Time)));
        Assert.Equal([1.0, 3.0, 1.0], keys.Select(k => k.Value));
        Assert.All(keys, k => Assert.Equal(Interpolation.Linear, k.Interpolation));
        for (int at = 0; at <= SpeedCurve.AtScale; at += 7)
        {
            Assert.Equal(at, SpeedCurveMath.AtOfClipTime(clip, SpeedCurveMath.ClipTimeOf(clip, at)));
        }

        SpeedCurve back = SpeedGraph.ToCurve(clip, keys);
        Assert.Equal(Three, back.Points);
        Assert.Equal(S(10), back.Span);
    }

    [Fact]
    public void LosExtremosNoSeMueven_YLaVelocidadSeAcotaComoEnElInspector()
    {
        Clip clip = Clip();
        List<Keyframe> keys = Keys(clip);
        keys[0] = keys[0] with { Time = keys[0].Time + S(2), Value = 50 };
        keys[1] = keys[1] with { Time = keys[1].Time - S(1), Value = 2.004 };
        keys[2] = keys[2] with { Time = keys[2].Time - S(3), Value = 0.001 };
        Assert.Equal([new(0, 1000), new(4000, 200), new(10_000, 10)], SpeedGraph.ToCurve(clip, keys).Points);
    }

    [Fact]
    public void UnTrozoDividido_SoloMuestraSuVentana_YConservaElResto()
    {
        Clip part = Clip(offset: S(5), duration: S(5));
        List<Keyframe> keys = Keys(part);
        Assert.Equal([3.0, 1.0], keys.Select(k => k.Value));
        keys[0] = keys[0] with { Value = 2 };
        Assert.Equal([new(0, 100), new(5000, 200), new(10_000, 100)], SpeedGraph.ToCurve(part, keys).Points);
    }

    [Fact]
    public void ElCatalogoOfreceLaVelocidadSoloConRampaEditable()
    {
        Clip clip = Clip();
        var context = new ParameterSpec.Context(1920, 1080, 10);
        GraphParameter speed = Assert.Single(GraphParameters.For(clip, context));
        Assert.Equal(SpeedGraph.EffectId, speed.EffectId);
        Assert.Equal("×", speed.Spec.Unit);
        (Clip? proxy, EffectInstance? effect) = GraphParameters.Resolve(clip, speed.EffectId);
        Assert.NotNull(proxy);
        Assert.Equal(3, effect!.Parameters[SpeedGraph.Name].Keyframes.Count);
        clip.Speed!.Freeze = true;
        Assert.Empty(GraphParameters.For(clip, context));
        clip.Speed = null;
        Assert.Empty(GraphParameters.For(clip, context));
        Assert.Null(GraphParameters.Resolve(null, speed.EffectId).Effect);
    }

    private static async Task<(EditorSession Session, SpeedEditor Editor)> SessionWithHero()
    {
        var session = new EditorSession(new FakePlayback(), new FakeProber().Video("a.mp4", 10), new FakeDialogs(), new SequentialIdGenerator());
        await session.OpenFilesAsync([Path.GetFullPath("a.mp4")]);
        session.Select([session.MainTrack!.Clips[0].Id]);
        var editor = new SpeedEditor(session, SpeedCommands.Default);
        Assert.True(editor.ChooseCurve(SpeedEditor.CurveOptions.ToList().FindIndex(o => o.Preset == SpeedPreset.Hero)));
        return (session, editor);
    }

    [Fact]
    public void MoverUnPuntoDesdeLaGrafica_DaLoMismoQueDesdeElInspectorDeVelocidad() => UiThread.Run(async () =>
    {
        (EditorSession inspector, SpeedEditor editor) = await SessionWithHero();
        (EditorSession graph, _) = await SessionWithHero();
        Clip before = graph.MainTrack!.Clips[0];
        int steps = graph.History.Count;

        // El inspector lleva el tercer punto (4300, 0,2×) a (4000, 0,5×).
        Assert.True(editor.BeginPointDrag(2));
        Assert.True(editor.DragPoint(4000, 50));
        editor.EndPointDrag();

        // La gráfica arrastra ese mismo punto como keyframe, en un gesto.
        Keyframe key = Keys(before)[2];
        Time delta = SpeedGraph.KeyTime(before, 4000) - key.Time;
        graph.BeginGesture("Mover keyframes");
        Assert.True(graph.TryExecute(KeyframeGraphEdits.Move(graph.SequenceId, before.Id, SpeedGraph.EffectId, SpeedGraph.Name,
            [key], Time.Zero, delta, 0.5 - key.Value, SpeedGraph.Spec, null, graph.SpeedScope)));
        graph.EndGesture();

        Clip fromInspector = inspector.MainTrack!.Clips[0];
        Clip fromGraph = graph.MainTrack!.Clips[0];
        Assert.Equal(new SpeedPoint(4000, 50), fromGraph.Speed!.Curve!.Points[2]);
        Assert.Equal(fromInspector.Speed!.Curve!.Points, fromGraph.Speed.Curve.Points);
        Assert.Equal(fromInspector.Duration, fromGraph.Duration);
        Assert.Equal(steps + 1, graph.History.Count);
        Assert.Equal(inspector.History.Count, graph.History.Count);

        // Supr quita el punto pero nunca los extremos, y se deshace de una vez.
        Time[] ends = [Keys(fromGraph)[0].Time, Keys(fromGraph)[^1].Time];
        Assert.True(graph.Execute(KeyframeGraphEdits.Delete(graph.SequenceId, before.Id, SpeedGraph.EffectId, SpeedGraph.Name,
            [Keys(fromGraph)[2].Time, .. ends], graph.SpeedScope)));
        Assert.Equal(5, graph.MainTrack.Clips[0].Speed!.Curve!.Points.Count);
        Assert.Equal(0, graph.MainTrack.Clips[0].Speed!.Curve!.Points[0].At);
        Assert.True(graph.Undo());
        Assert.Equal(fromInspector.Speed.Curve.Points, graph.MainTrack.Clips[0].Speed!.Curve!.Points);
    });

    [Fact]
    public void ArrastrarEnVariosPasos_PorLaGrafica_NoDuplicaPuntos_YDaLoMismoQueUnPaso() => UiThread.Run(async () =>
    {
        (EditorSession steps, _) = await SessionWithHero();
        (EditorSession once, _) = await SessionWithHero();
        Clip before = steps.MainTrack!.Clips[0];
        int points = before.Speed!.Curve!.Points.Count;
        int history = steps.History.Count;
        Keyframe key = Keys(before)[2];
        Time halfway = SpeedGraph.KeyTime(before, 4150) - key.Time;
        Time final = SpeedGraph.KeyTime(before, 4000) - key.Time;
        double lift = 0.5 - key.Value;

        // El ratón pasa por un punto intermedio antes de soltar: cada paso parte de la rampa del inicio del gesto.
        SequenceSnapshot start = SequenceSnapshot.Capture(steps.Sequence);
        steps.BeginGesture("Mover keyframes");
        Assert.True(steps.TryExecute(KeyframeGraphEdits.Move(steps.SequenceId, before.Id, SpeedGraph.EffectId, SpeedGraph.Name,
            [key], Time.Zero, halfway, lift / 2, SpeedGraph.Spec, null, steps.SpeedScope, start)));
        Assert.True(steps.TryExecute(KeyframeGraphEdits.Move(steps.SequenceId, before.Id, SpeedGraph.EffectId, SpeedGraph.Name,
            [key], halfway, final, lift, SpeedGraph.Spec, null, steps.SpeedScope, start)));
        steps.EndGesture();

        once.BeginGesture("Mover keyframes");
        Assert.True(once.TryExecute(KeyframeGraphEdits.Move(once.SequenceId, before.Id, SpeedGraph.EffectId, SpeedGraph.Name,
            [key], Time.Zero, final, lift, SpeedGraph.Spec, null, once.SpeedScope, SequenceSnapshot.Capture(once.Sequence))));
        once.EndGesture();

        Clip stepped = steps.MainTrack.Clips[0];
        Assert.Equal(points, stepped.Speed!.Curve!.Points.Count);
        Assert.Equal(new SpeedPoint(4000, 50), stepped.Speed.Curve.Points[2]);
        Assert.Equal(once.MainTrack!.Clips[0].Speed!.Curve!.Points, stepped.Speed.Curve.Points);
        Assert.Equal(once.MainTrack.Clips[0].Duration, stepped.Duration);
        Assert.Equal(history + 1, steps.History.Count);
    });
}
