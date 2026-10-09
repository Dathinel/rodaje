using CsCheck;
using FotoToma.Core.Model;
using FotoToma.Core.Timing;

namespace FotoToma.Core.Tests.Model;

public class ParameterTests
{
    private static readonly Time Span = Time.FromSeconds(4);

    private static Parameter TwoKeyframes(Interpolation interpolation, Interpolation right = Interpolation.Linear) => new()
    {
        Value = -1,
        Keyframes = [new Keyframe(Time.Zero, 10, interpolation), new Keyframe(Span, 20, right)],
    };

    private static Time At(double fraction) => Time.FromTicks((long)(Span.Ticks * fraction));

    [Fact]
    public void WithoutKeyframes_ReturnsValue()
    {
        var p = new Parameter { Value = 0.75 };
        Assert.Equal(0.75, p.Evaluate(Time.Zero));
        Assert.Equal(0.75, p.Evaluate(Time.FromSeconds(-3)));
        Assert.Equal(0.75, p.Evaluate(Time.FromSeconds(1e6)));
        Assert.Equal(0, new Parameter().Evaluate(Time.Zero));
    }

    [Fact]
    public void SingleKeyframe_IsConstantEverywhere()
    {
        var p = new Parameter { Value = 5, Keyframes = [new Keyframe(Time.FromSeconds(2), 9)] };
        Assert.Equal(9, p.Evaluate(Time.Zero));
        Assert.Equal(9, p.Evaluate(Time.FromSeconds(2)));
        Assert.Equal(9, p.Evaluate(Time.FromSeconds(100)));
    }

    [Fact]
    public void BeforeFirstAndAfterLast_ClampToEndKeyframes()
    {
        Parameter p = TwoKeyframes(Interpolation.Linear);
        p.Keyframes[0] = p.Keyframes[0] with { Time = Time.FromSeconds(1) };
        Assert.Equal(10, p.Evaluate(Time.Zero));
        Assert.Equal(10, p.Evaluate(Time.FromSeconds(-10)));
        Assert.Equal(20, p.Evaluate(Span + Time.FromTicks(1)));
        Assert.Equal(20, p.Evaluate(Time.FromSeconds(1000)));
    }

    [Fact]
    public void AtExactKeyframeTimes_ReturnsKeyframeValues()
    {
        var p = new Parameter
        {
            Keyframes =
            [
                new Keyframe(Time.FromSeconds(0), 1, Interpolation.EaseIn),
                new Keyframe(Time.FromSeconds(1), 2, Interpolation.Hold),
                new Keyframe(Time.FromSeconds(2), 3, Interpolation.EaseInOut),
                new Keyframe(Time.FromSeconds(3), 4),
            ],
        };
        Assert.Equal(1, p.Evaluate(Time.FromSeconds(0)));
        Assert.Equal(2, p.Evaluate(Time.FromSeconds(1)));
        Assert.Equal(3, p.Evaluate(Time.FromSeconds(2)));
        Assert.Equal(4, p.Evaluate(Time.FromSeconds(3)));
    }

    [Theory]
    [InlineData(Interpolation.Hold, 10, 10, 10, 10, 20)]
    [InlineData(Interpolation.Linear, 10, 12.5, 15, 17.5, 20)]
    [InlineData(Interpolation.EaseIn, 10, 10.15625, 11.25, 14.21875, 20)]
    [InlineData(Interpolation.EaseOut, 10, 15.78125, 18.75, 19.84375, 20)]
    [InlineData(Interpolation.EaseInOut, 10, 10.625, 15, 19.375, 20)]
    public void BetweenTwoKeyframes_FollowsLeftInterpolation(
        Interpolation interpolation, double at0, double at25, double at50, double at75, double at100)
    {
        // La interpolación del keyframe derecho no influye en el tramo
        foreach (Interpolation right in Enum.GetValues<Interpolation>())
        {
            Parameter p = TwoKeyframes(interpolation, right);
            Assert.Equal(at0, p.Evaluate(At(0)), 12);
            Assert.Equal(at25, p.Evaluate(At(0.25)), 12);
            Assert.Equal(at50, p.Evaluate(At(0.5)), 12);
            Assert.Equal(at75, p.Evaluate(At(0.75)), 12);
            Assert.Equal(at100, p.Evaluate(At(1)), 12);
        }
    }

    [Fact]
    public void Hold_KeepsLeftValueUntilTheTickOfNextKeyframe()
    {
        Parameter p = TwoKeyframes(Interpolation.Hold);
        Assert.Equal(10, p.Evaluate(Span - Time.FromTicks(1)));
        Assert.Equal(20, p.Evaluate(Span));
    }

    [Fact]
    public void ThreeKeyframes_UseTheSegmentContainingTheInstant()
    {
        var p = new Parameter
        {
            Keyframes =
            [
                new Keyframe(Time.FromSeconds(0), 0, Interpolation.Linear),
                new Keyframe(Time.FromSeconds(2), 100, Interpolation.Hold),
                new Keyframe(Time.FromSeconds(4), -100, Interpolation.Linear),
            ],
        };
        Assert.Equal(50, p.Evaluate(Time.FromSeconds(1)), 12);
        Assert.Equal(100, p.Evaluate(Time.FromSeconds(3)));
        Assert.Equal(-100, p.Evaluate(Time.FromSeconds(4)));
    }

    [Fact]
    public void DecreasingValues_Interpolate()
    {
        var p = new Parameter { Keyframes = [new Keyframe(Time.Zero, 1), new Keyframe(Time.FromSeconds(2), 0)] };
        Assert.Equal(0.5, p.Evaluate(Time.FromSeconds(1)), 12);
    }

    [Fact]
    public void UnknownInterpolation_Throws()
    {
        var p = new Parameter
        {
            Keyframes = [new Keyframe(Time.Zero, 0, (Interpolation)42), new Keyframe(Time.FromSeconds(1), 1)],
        };
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => p.Evaluate(Time.FromSeconds(0.5)));
        Assert.Equal("interpolation", ex.ParamName);
        Assert.Equal((Interpolation)42, ex.ActualValue);
        Assert.Throws<ArgumentOutOfRangeException>(() => Parameter.Ease((Interpolation)(-1), 0.5));

        // En los extremos no hace falta interpolar
        Assert.Equal(0, p.Evaluate(Time.Zero));
        Assert.Equal(1, p.Evaluate(Time.FromSeconds(1)));
    }

    [Fact]
    public void Ease_Hold_IsAlwaysZero()
    {
        Gen.Double[0, 1].Sample(t => Assert.Equal(0, Parameter.Ease(Interpolation.Hold, t)));
    }

    [Theory]
    [InlineData(Interpolation.Linear)]
    [InlineData(Interpolation.EaseIn)]
    [InlineData(Interpolation.EaseOut)]
    [InlineData(Interpolation.EaseInOut)]
    public void Ease_StartsAtZeroEndsAtOneAndIsMonotonic(Interpolation interpolation)
    {
        Assert.Equal(0, Parameter.Ease(interpolation, 0), 15);
        Assert.Equal(1, Parameter.Ease(interpolation, 1), 15);

        Gen.Select(Gen.Double[0, 1], Gen.Double[0, 1]).Sample((x, y) =>
        {
            double lo = Math.Min(x, y);
            double hi = Math.Max(x, y);
            double eLo = Parameter.Ease(interpolation, lo);
            double eHi = Parameter.Ease(interpolation, hi);
            Assert.True(eLo <= eHi, $"Ease({lo}) = {eLo} > Ease({hi}) = {eHi}");
            Assert.InRange(eLo, 0, 1);
            Assert.InRange(eHi, 0, 1);
        });
    }

    [Fact]
    public void Evaluate_IsMonotonicBetweenIncreasingKeyframes()
    {
        Gen.Select(Gen.Int[1, 4], Gen.Long[0, Span.Ticks], Gen.Long[0, Span.Ticks]).Sample((kind, a, b) =>
        {
            Parameter p = TwoKeyframes((Interpolation)kind);
            double va = p.Evaluate(Time.FromTicks(Math.Min(a, b)));
            double vb = p.Evaluate(Time.FromTicks(Math.Max(a, b)));
            Assert.True(va <= vb);
            Assert.InRange(va, 10, 20);
        });
    }

    [Fact]
    public void Clone_CopiesKeyframeListIndependently()
    {
        Parameter p = TwoKeyframes(Interpolation.EaseIn);
        Parameter copy = p.Clone();

        Assert.NotSame(p, copy);
        Assert.Equal(p.Value, copy.Value);
        Assert.NotSame(p.Keyframes, copy.Keyframes);
        Assert.Equal(p.Keyframes, copy.Keyframes);

        copy.Value = 3;
        copy.Keyframes.RemoveAt(0);
        Assert.Equal(-1, p.Value);
        Assert.Equal(2, p.Keyframes.Count);
    }

    [Fact]
    public void Keyframe_DefaultsToLinearAndHasValueEquality()
    {
        var k = new Keyframe(Time.FromSeconds(1), 2);
        Assert.Equal(Interpolation.Linear, k.Interpolation);
        Assert.Equal(new Keyframe(Time.FromSeconds(1), 2, Interpolation.Linear), k);
        Assert.NotEqual(k with { Interpolation = Interpolation.Hold }, k);
    }
}

public class EffectInstanceTests
{
    [Fact]
    public void NewEffect_HasDefaults()
    {
        var e = new EffectInstance();
        Assert.Equal("", e.Id);
        Assert.Equal("", e.Type);
        Assert.True(e.Enabled);
        Assert.Empty(e.Parameters);
    }

    [Fact]
    public void Clone_IsDeep()
    {
        var e = new EffectInstance
        {
            Id = "fx",
            Type = "fototoma.volume",
            Enabled = false,
            Parameters =
            {
                ["gain"] = new Parameter { Value = 0.5, Keyframes = [new Keyframe(Time.Zero, 0)] },
            },
        };
        EffectInstance copy = e.Clone();

        Assert.NotSame(e, copy);
        Assert.Equal(("fx", "fototoma.volume", false), (copy.Id, copy.Type, copy.Enabled));
        Assert.NotSame(e.Parameters, copy.Parameters);
        Assert.NotSame(e.Parameters["gain"], copy.Parameters["gain"]);

        copy.Parameters["gain"].Value = 2;
        copy.Parameters["gain"].Keyframes.Add(new Keyframe(Time.FromSeconds(1), 1));
        copy.Parameters.Remove("gain");
        Assert.Equal(0.5, e.Parameters["gain"].Value);
        Assert.Single(e.Parameters["gain"].Keyframes);
    }
}
