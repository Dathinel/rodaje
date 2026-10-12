using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Core.Timing;
using static Rodaje.Core.Tests.Commands.CommandFixtures;
using T = Rodaje.Core.Model.BuiltInEffects.TransformParameters;

namespace Rodaje.Core.Tests.Commands;

/// <summary>Curvas Bézier, trayectorias, mover keyframes y animaciones de fábrica: un paso cada uno, exacto al deshacer.</summary>
public sealed class KeyframeCurveEditsTests
{
    private const string Clip2 = "clip-2";
    private const string Fx = "fx-1";

    private static Parameter Param(Project project, string name) =>
        Seq(project).GetClip(Clip2).Clip.Effects.Single(e => e.Id == Fx).Parameters[name];

    private static void RoundTrip(UndoStack stack, ICommand command)
    {
        string before = ProjectDump.Of(stack.Project);
        stack.Execute(command);
        string after = ProjectDump.Of(stack.Project);
        Assert.NotEqual(before, after);
        Assert.Empty(ModelValidator.Validate(stack.Project));
        Assert.True(stack.Undo());
        Assert.Equal(before, ProjectDump.Of(stack.Project));
        Assert.True(stack.Redo());
        Assert.Equal(after, ProjectDump.Of(stack.Project));
    }

    [Fact]
    public void SetKeyframeEase_PasaABezier_YCambiarElValorLaConserva()
    {
        Project project = Create();
        var stack = new UndoStack(project);
        var ease = new BezierEase(0.1, 1.4, 0.3, 1);
        RoundTrip(stack, Edits.SetKeyframeEase(SequenceId, Clip2, Fx, T.Opacity, S(10), ease));
        Keyframe key = Param(project, T.Opacity).Keyframes[0];
        Assert.Equal(Interpolation.Bezier, key.Interpolation);
        Assert.Equal(ease, key.Ease);

        stack.Execute(Edits.SetParameter(SequenceId, Clip2, Fx, T.Opacity, 0.4, S(10)));
        Assert.Equal(ease, Param(project, T.Opacity).Keyframes[0].Ease);
        Assert.Equal(0.4, Param(project, T.Opacity).Keyframes[0].Value);
    }

    [Fact]
    public void SetKeyframeEase_FueraDeRango_SeAcota_YLosArrastresSeFunden()
    {
        Project project = Create();
        var stack = new UndoStack(project);
        stack.Execute(Edits.SetKeyframeEase(SequenceId, Clip2, Fx, T.Opacity, S(10), new BezierEase(-1, 9, 2, -9)));
        stack.Execute(Edits.SetKeyframeEase(SequenceId, Clip2, Fx, T.Opacity, S(10), new BezierEase(0.5, 2, 0.5, -1)));
        Assert.Equal(new BezierEase(0.5, 2, 0.5, -1), Param(project, T.Opacity).Keyframes[0].Ease);
        Assert.Equal(1, stack.Count);

        stack.Execute(Edits.SetKeyframeEase(SequenceId, Clip2, Fx, T.Opacity, S(12), new BezierEase(-1, 9, 2, -9)));
        Assert.Equal(new BezierEase(0, BezierEase.MaxY, 1, BezierEase.MinY), Param(project, T.Opacity).Keyframes[1].Ease);
    }

    [Fact]
    public void SetPathTangents_FijaXeYEnUnPaso_YLaPosicionSigueLaCurva()
    {
        Project project = Create();
        var stack = new UndoStack(project);
        stack.Execute(Edits.SetKeyframe(SequenceId, Clip2, Fx, T.X, S(10), 0));
        stack.Execute(Edits.SetKeyframe(SequenceId, Clip2, Fx, T.X, S(12), 100));
        stack.Execute(Edits.SetKeyframe(SequenceId, Clip2, Fx, T.Y, S(10), 0));
        stack.Execute(Edits.SetKeyframe(SequenceId, Clip2, Fx, T.Y, S(12), 0));
        RoundTrip(stack, Edits.SetPathTangents(SequenceId, Clip2, Fx, S(10), null, (0, -200)));
        stack.Execute(Edits.SetPathTangents(SequenceId, Clip2, Fx, S(12), (0, -200), null));

        Assert.Equal(-200, Param(project, T.Y).Keyframes[0].SpatialOut);
        Assert.Equal(0, Param(project, T.X).Keyframes[0].SpatialOut);
        Assert.True(MotionPath.TryEvaluate(Param(project, T.X), Param(project, T.Y), S(11), out double x, out double y));
        Assert.Equal(50, x, 6);
        Assert.Equal(-150, y, 6);

        string json = ProjectSerializer.Serialize(project);
        Assert.Contains("\"spatialOut\": -200", json, StringComparison.Ordinal);
        Assert.Equal(json, ProjectSerializer.Serialize(ProjectSerializer.Deserialize(json)));
    }

    [Fact]
    public void SalirDeBezier_QuitaLosPuntosDeControl()
    {
        Project project = Create();
        var stack = new UndoStack(project);
        stack.Execute(Edits.SetKeyframeEase(SequenceId, Clip2, Fx, T.Opacity, S(10), new BezierEase(0.1, 1.4, 0.3, 1)));
        stack.Execute(Edits.SetKeyframeInterpolation(SequenceId, Clip2, Fx, T.Opacity, S(10), Interpolation.Bounce));
        Assert.Null(Param(project, T.Opacity).Keyframes[0].Ease);
        Assert.DoesNotContain("\"ease\"", ProjectSerializer.Serialize(project), StringComparison.Ordinal);
    }

    [Fact]
    public void MoveKeyframe_NoPasaASusVecinos()
    {
        Project project = Create();
        var stack = new UndoStack(project);
        RoundTrip(stack, Edits.MoveKeyframe(SequenceId, Clip2, Fx, T.Opacity, S(12), S(11), "mover"));
        Assert.Equal(S(11), Param(project, T.Opacity).Keyframes[1].Time);
        Assert.Throws<EditException>(() => stack.Execute(Edits.MoveKeyframe(SequenceId, Clip2, Fx, T.Opacity, S(11), S(10), "otro")));
    }

    [Fact]
    public void ReplaceKeyframes_ExigeOrden_YConUnoQuedaFijo()
    {
        Project project = Create();
        var ctx = new EditContext(project, Seq(project), new SequentialIdGenerator());
        EffectEdits.ReplaceKeyframes(ctx, Clip2, Fx, T.Scale, [new Keyframe(S(10), 1.5)]);
        Assert.Empty(Param(project, T.Scale).Keyframes);
        Assert.Equal(1.5, Param(project, T.Scale).Value);

        Assert.Throws<EditException>(() => EffectEdits.ReplaceKeyframes(ctx, Clip2, Fx, T.Scale, [new Keyframe(S(11), 1), new Keyframe(S(10), 2)]));
    }

    [Theory]
    [InlineData(MotionPresets.KenBurns)]
    [InlineData(MotionPresets.SlowZoom)]
    [InlineData(MotionPresets.SlideIn)]
    [InlineData(MotionPresets.SlideOut)]
    [InlineData(MotionPresets.Shake)]
    [InlineData(MotionPresets.Heartbeat)]
    [InlineData(MotionPresets.Spin)]
    [InlineData(MotionPresets.PunchZoom)]
    public void Presets_DanKeyframesOrdenadosDentroDelClip(string id)
    {
        foreach (double seconds in new[] { 0.3, 2.5, 60, 3600 })
        {
            IReadOnlyDictionary<string, IReadOnlyList<MotionKey>> keys = MotionPresets.Build(id, 1920, 1080, seconds, new MotionBase(10, -20, 1.2, 5));
            Assert.NotEmpty(keys);
            foreach ((string name, IReadOnlyList<MotionKey> list) in keys)
            {
                Assert.InRange(list.Count, 1, MotionPresets.MaxKeys);
                Assert.All(list, k => Assert.InRange(k.Seconds, 0, seconds + 1e-9));
                Assert.All(list.Zip(list.Skip(1)), p => Assert.True(p.Second.Seconds > p.First.Seconds, $"{id}/{name} desordenado"));
                Assert.All(list, k => Assert.True(double.IsFinite(k.Value)));
            }
        }
    }

    [Fact]
    public void Presets_SonRelativosALaTransformacionDePartida()
    {
        var from = new MotionBase(100, 50, 2, 10);
        IReadOnlyDictionary<string, IReadOnlyList<MotionKey>> burns = MotionPresets.Build(MotionPresets.KenBurns, 1000, 500, 4, from);
        Assert.Equal(2, burns[T.Scale][0].Value);
        Assert.Equal(2 * 1.15, burns[T.Scale][^1].Value, 9);
        Assert.Equal(100 - 40, burns[T.X][^1].Value, 9);

        IReadOnlyList<MotionKey> slide = MotionPresets.Build(MotionPresets.SlideIn, 1000, 500, 4, from)[T.X];
        Assert.Equal((-900, 100), (slide[0].Value, slide[^1].Value));
        Assert.Equal(MotionPresets.SlideSeconds, slide[^1].Seconds);

        IReadOnlyList<MotionKey> spin = MotionPresets.Build(MotionPresets.Spin, 1000, 500, 4, from)[T.Rotation];
        Assert.Equal((10, 370), (spin[0].Value, spin[^1].Value));
    }

    [Fact]
    public void Shake_EsDeterminista_YNoPasaDeSuAmplitud()
    {
        IReadOnlyDictionary<string, IReadOnlyList<MotionKey>> a = MotionPresets.Build(MotionPresets.Shake, 1920, 1080, 3, MotionBase.Default);
        IReadOnlyDictionary<string, IReadOnlyList<MotionKey>> b = MotionPresets.Build(MotionPresets.Shake, 1920, 1080, 3, MotionBase.Default);
        Assert.Equal(a[T.X], b[T.X]);
        Assert.Equal(37, a[T.X].Count);
        Assert.All(a[T.X], k => Assert.InRange(Math.Abs(k.Value), 0, 0.012 * 1920));
        Assert.All(a[T.Rotation], k => Assert.InRange(Math.Abs(k.Value), 0, 0.8));
        Assert.Equal(1.05, a[T.Scale].Single().Value);
        Assert.Equal((0.0, 0.0), (a[T.X][0].Value, a[T.X][^1].Value));
    }

    [Fact]
    public void Heartbeat_DosPulsosPorSegundo()
    {
        IReadOnlyList<MotionKey> keys = MotionPresets.Build(MotionPresets.Heartbeat, 1920, 1080, 3, MotionBase.Default)[T.Scale];
        Assert.Equal(3 * 2, keys.Count(k => k.Value > 1));
        Assert.Equal(1.08, keys.Max(k => k.Value), 9);
    }
}
