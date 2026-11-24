using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Tests.Model;

public sealed class KeyframeInvariantsTests
{
    public static TheoryData<Interpolation> Curves => new(Enum.GetValues<Interpolation>());

    [Theory]
    [MemberData(nameof(Curves))]
    public void Every_key_is_exact_and_extrapolation_holds_the_end_values(Interpolation interpolation)
    {
        Parameter parameter = Pair(interpolation, -128, 256);
        parameter.Keyframes.Add(new Keyframe(Time.FromTicks(3000), 512));
        foreach (Keyframe key in parameter.Keyframes)
        {
            Assert.Equal(key.Value, parameter.Evaluate(key.Time));
        }

        Assert.Equal(-128, parameter.Evaluate(Time.FromTicks(long.MinValue)));
        Assert.Equal(512, parameter.Evaluate(Time.FromTicks(long.MaxValue)));
    }

    [Theory]
    [InlineData(Interpolation.Hold, false)]
    [InlineData(Interpolation.Linear, false)]
    [InlineData(Interpolation.EaseIn, false)]
    [InlineData(Interpolation.EaseOut, false)]
    [InlineData(Interpolation.EaseInOut, false)]
    [InlineData(Interpolation.Bezier, false)]
    [InlineData(Interpolation.Hold, true)]
    [InlineData(Interpolation.Linear, true)]
    [InlineData(Interpolation.EaseIn, true)]
    [InlineData(Interpolation.EaseOut, true)]
    [InlineData(Interpolation.EaseInOut, true)]
    [InlineData(Interpolation.Bezier, true)]
    public void Monotone_curves_stay_ordered_between_increasing_and_decreasing_keys(Interpolation interpolation, bool decreasing)
    {
        Parameter parameter = Pair(interpolation, decreasing ? 100 : -100, decreasing ? -100 : 100);
        double previous = parameter.Evaluate(Time.FromTicks(1000));
        for (int tick = 1001; tick <= 2000; tick++)
        {
            double current = parameter.Evaluate(Time.FromTicks(tick));
            Assert.True(double.IsFinite(current));
            Assert.InRange(current, -100, 100);
            Assert.True(decreasing ? current <= previous : current >= previous,
                $"Curva {interpolation}, tick {tick}: {previous} → {current}.");
            previous = current;
        }
    }

    [Theory]
    [MemberData(nameof(Curves))]
    public void A_single_key_is_constant_even_at_extreme_times(Interpolation interpolation)
    {
        var parameter = new Parameter { Value = -5, Keyframes = [new Keyframe(Time.FromTicks(17), 1e300, interpolation)] };
        foreach (long tick in new[] { long.MinValue, -1L, 0, 16, 17, 18, long.MaxValue })
        {
            Assert.Equal(1e300, parameter.Evaluate(Time.FromTicks(tick)));
        }
    }

    [Theory]
    [MemberData(nameof(Curves))]
    public void Duplicate_instants_are_rejected_by_the_model_instead_of_dividing_by_zero(Interpolation interpolation)
    {
        var parameter = new Parameter { Keyframes = [new Keyframe(Time.Zero, 10, interpolation), new Keyframe(Time.Zero, 20)] };
        Project project = Rodaje.Core.Tests.Serialization.SerializationSamples.Rich();
        project.Sequences[0].VideoTracks[0].Clips[0].Effects[0].Parameters["opacity"] = parameter;
        Assert.Contains(ModelValidator.Validate(project), message => message.Contains("keyframe", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(10, parameter.Evaluate(Time.Zero));
        Assert.Equal(20, parameter.Evaluate(Time.FromTicks(1)));
    }

    [Theory]
    [MemberData(nameof(Curves))]
    public void Huge_finite_values_never_produce_nan(Interpolation interpolation)
    {
        Parameter parameter = Pair(interpolation, -1e300, 1e300);
        for (int tick = 0; tick <= 3000; tick++)
        {
            Assert.True(double.IsFinite(parameter.Evaluate(Time.FromTicks(tick))), $"Curva {interpolation}, tick {tick}.");
        }
    }

    [Theory]
    [InlineData(Interpolation.Linear)]
    [InlineData(Interpolation.Hold)]
    public void Opposite_double_extremes_never_produce_nan(Interpolation interpolation)
    {
        Parameter parameter = Pair(interpolation, -double.MaxValue, double.MaxValue);
        // La resta de extremos finitos desborda: sin cuidado, Hold devolvía NaN y Linear infinito en vez de cero.
        double actual = parameter.Evaluate(Time.FromTicks(1500));
        Assert.False(double.IsNaN(actual), $"Curva {interpolation}: evaluar extremos finitos devuelve NaN.");
        if (interpolation == Interpolation.Linear)
        {
            Assert.Equal(0, actual);
        }
    }

    private static Parameter Pair(Interpolation interpolation, double from, double to) => new()
    {
        Keyframes = [new Keyframe(Time.FromTicks(1000), from, interpolation) { Ease = BezierEase.Smooth },
            new Keyframe(Time.FromTicks(2000), to)],
    };
}
