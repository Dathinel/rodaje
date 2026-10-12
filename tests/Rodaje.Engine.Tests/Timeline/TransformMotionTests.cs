using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Engine.Tests.Support;
using Rodaje.Engine.Timeline;
using Rodaje.Playback.Composition;
using static Rodaje.Engine.Tests.Support.Seconds;
using T = Rodaje.Core.Model.BuiltInEffects.TransformParameters;

namespace Rodaje.Engine.Tests.Timeline;

/// <summary>La transformación ampliada en el motor: mismas curvas que el núcleo, forma de la capa y muestras del obturador.</summary>
public sealed class TransformMotionTests
{
    private static readonly Interpolation[] Kinds = Enum.GetValues<Interpolation>();

    private static Keyframe KeyAt(Random random, long ticks)
    {
        Interpolation kind = Kinds[random.Next(Kinds.Length)];
        return new Keyframe(Time.FromTicks(ticks), (random.NextDouble() * 1000) - 500, kind)
        {
            Ease = kind == Interpolation.Bezier
                ? new BezierEase(random.NextDouble(), (random.NextDouble() * 5) - 2, random.NextDouble(), (random.NextDouble() * 5) - 2)
                : null,
            SpatialIn = random.Next(2) == 0 ? null : (random.NextDouble() * 600) - 300,
            SpatialOut = random.Next(2) == 0 ? null : (random.NextDouble() * 600) - 300,
        };
    }

    private static Parameter Animated(Random random) => new()
    {
        Keyframes = [KeyAt(random, 0), KeyAt(random, Time.TicksPerSecond), KeyAt(random, 3 * Time.TicksPerSecond), KeyAt(random, 4 * Time.TicksPerSecond)],
    };

    private static long At(Random random) => random.NextInt64(-Time.TicksPerSecond, 5 * Time.TicksPerSecond);

    [Fact]
    public void Curve_GivesTheSameNumbersAsTheCore_WithEveryEasingAndTangent()
    {
        var random = new Random(7);
        for (int i = 0; i < 5000; i++)
        {
            Parameter parameter = Animated(random);
            long at = At(random);
            Curve curve = Curve.From(parameter, 0, static v => v, static t => t);
            Assert.Equal(parameter.Evaluate(Time.FromTicks(at)), curve.Evaluate(at));
        }
    }

    [Fact]
    public void Position_FollowsTheSamePathAsTheCore()
    {
        var random = new Random(11);
        int paths = 0;
        for (int i = 0; i < 5000; i++)
        {
            Parameter x = Animated(random);
            Parameter y = Animated(random);
            long at = At(random);
            Curve cx = Curve.From(x, 0, static v => v, static t => t);
            Curve cy = Curve.From(y, 0, static v => v, static t => t);
            (double px, double py) = Curve.Position(cx, cy, at);
            if (MotionPath.TryEvaluate(x, y, Time.FromTicks(at), out double ex, out double ey))
            {
                paths++;
                Assert.Equal((ex, ey), (px, py));
            }
            else
            {
                Assert.Equal((x.Evaluate(Time.FromTicks(at)), y.Evaluate(Time.FromTicks(at))), (px, py));
            }
        }

        Assert.True(paths > 1000, $"{paths} instantes sobre una trayectoria");
    }

    [Fact]
    public void Overshooting_curves_stay_in_range_and_report_their_real_maximum()
    {
        var scale = new Parameter
        {
            Keyframes = [new Keyframe(S(0), 1, Interpolation.Elastic), new Keyframe(S(1), 2)],
        };
        var opacity = new Parameter
        {
            Keyframes = [new Keyframe(S(0), 0, Interpolation.Elastic), new Keyframe(S(1), 1)],
        };
        Curve s = Curve.From(scale, 1, static v => Math.Clamp(v, 0, 100), static t => t);
        Curve o = Curve.From(opacity, 1, static v => Math.Clamp(v, 0, 1), static t => t);
        Assert.True(s.Max > 2.3, $"máximo {s.Max}");
        for (long t = 0; t <= Time.TicksPerSecond; t += Time.TicksPerSecond / 100)
        {
            Assert.InRange(o.Evaluate(t), 0, 1);
        }
    }

    [Fact]
    public void The_layer_carries_its_shape_and_the_shutter_samples_around_the_frame()
    {
        var backend = new FakeTimelineBackend();
        var b = new TimelineBuilder(1, 0);
        b.AddMedia("m", Rational.Fps30, 3000, backend: backend);
        Clip clip = b.AddVideo(0, "m", Time.Zero, Time.Zero, S(4));
        EffectInstance transform = TimelineEffectsTests.Effect(BuiltInEffects.Transform, (T.SkewX, 12), (T.AnchorX, 30), (T.ScaleY, 0.5),
            (T.ShutterAngle, 180), (T.MotionSamples, 4));
        transform.Options[BuiltInEffects.TransformOptions.FlipY] = "on";
        transform.Parameters[T.X] = new Parameter { Keyframes = [new Keyframe(S(0), 0), new Keyframe(S(4), 1200)] };
        clip.Effects.Add(transform);
        using TimelineSession session = TimelineSessionTests.Open(b, backend);

        session.Seek(S(2));
        using CompositionFrame frame = session.Tick();
        LayerFrame layer = Assert.Single(frame.Layers);
        Assert.False(layer.IsPlain);
        Assert.Equal(new LayerShape(30, 0, 1, 0.5, 12, 0, false, true), layer.Transform.Shape);
        Assert.Equal(600, layer.Transform.X, 6);

        // 180° a 30 fps: el obturador abre 1/60 s centrado en el cuadro; 4 muestras en el centro de cada cuarto.
        IReadOnlyList<LayerTransform> samples = layer.MotionSamples!;
        Assert.Equal(4, samples.Count);
        double perSecond = 1200 / 4.0;
        double[] expected = [.. Enumerable.Range(0, 4).Select(k => 600 + (perSecond * (((k + 0.5) / 4) - 0.5) / 60))];
        Assert.Equal(expected, samples.Select(s => s.X), (a, e) => Math.Abs(a - e) < 1e-6);
        Assert.All(samples, s => Assert.Equal(layer.Transform.Shape, s.Shape));
    }

    [Fact]
    public void A_still_layer_with_a_shutter_has_no_samples_and_keeps_the_fast_path()
    {
        var backend = new FakeTimelineBackend();
        var b = new TimelineBuilder(1, 0);
        b.AddMedia("m", Rational.Fps30, 3000, backend: backend);
        Clip clip = b.AddVideo(0, "m", Time.Zero, Time.Zero, S(4));
        clip.Effects.Add(TimelineEffectsTests.Effect(BuiltInEffects.Transform, (T.Scale, 0.8), (T.ShutterAngle, 360)));
        using TimelineSession session = TimelineSessionTests.Open(b, backend);

        using CompositionFrame frame = session.Tick();
        LayerFrame layer = Assert.Single(frame.Layers);
        Assert.Null(layer.MotionSamples);
        Assert.True(layer.IsPlain);
        Assert.Null(layer.Transform.Shape);
    }
}
