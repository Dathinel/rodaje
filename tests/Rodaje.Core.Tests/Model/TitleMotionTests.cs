using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Tests.Model;

/// <summary>
/// Pruebas unitarias de <see cref="TitleMotion"/>: cálculo de duración en ticks de animaciones,
/// ajuste proporcional (Fit) cuando la entrada y la salida exceden el clip, y composición
/// de transformaciones delta con la del clip.
/// </summary>
public sealed class TitleMotionTests
{
    [Fact]
    public void Ticks_ReturnsZero_WhenAnimationIsNull()
    {
        Assert.Equal(0, TitleMotion.Ticks(null));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-0.5)]
    [InlineData(-10.0)]
    public void Ticks_ReturnsZero_WhenDurationIsNonPositive(double duration)
    {
        var animation = new StyleAnimation
        {
            DurationSeconds = duration,
            Curves = new() { ["x"] = [new StyleKey(0, 0)] },
        };

        Assert.Equal(0, TitleMotion.Ticks(animation));
    }

    [Fact]
    public void Ticks_ReturnsZero_WhenDurationIsNotFinite()
    {
        var nanAnimation = new StyleAnimation
        {
            DurationSeconds = double.NaN,
            Curves = new() { ["x"] = [new StyleKey(0, 0)] },
        };
        var infAnimation = new StyleAnimation
        {
            DurationSeconds = double.PositiveInfinity,
            Curves = new() { ["x"] = [new StyleKey(0, 0)] },
        };

        Assert.Equal(0, TitleMotion.Ticks(nanAnimation));
        Assert.Equal(0, TitleMotion.Ticks(infAnimation));
    }

    [Fact]
    public void Ticks_ReturnsZero_WhenCurvesAreEmptyOrContainNoKeys()
    {
        var noCurves = new StyleAnimation
        {
            DurationSeconds = 1.0,
            Curves = [],
        };
        var emptyKeyLists = new StyleAnimation
        {
            DurationSeconds = 1.0,
            Curves = new() { ["x"] = [], ["y"] = [] },
        };

        Assert.Equal(0, TitleMotion.Ticks(noCurves));
        Assert.Equal(0, TitleMotion.Ticks(emptyKeyLists));
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(0.5)]
    [InlineData(2.25)]
    public void Ticks_ReturnsConvertedTicks_WhenValidAnimationHasKeys(double seconds)
    {
        var animation = new StyleAnimation
        {
            DurationSeconds = seconds,
            Curves = new()
            {
                ["empty"] = [],
                [BuiltInEffects.TransformParameters.Opacity] = [new StyleKey(0, 0), new StyleKey(seconds, 1)],
            },
        };

        long expected = Time.FromSeconds(seconds).Ticks;
        Assert.Equal(expected, TitleMotion.Ticks(animation));
    }

    [Theory]
    [InlineData(100, 200, 500)]
    [InlineData(250, 250, 500)]
    [InlineData(0, 300, 500)]
    [InlineData(400, 0, 500)]
    public void Fit_PreservesTicksAndUnitStretch_WhenTotalFitsWithinDuration(long inTicks, long outTicks, long durationTicks)
    {
        (long fittedIn, long fittedOut, double stretch) = TitleMotion.Fit(inTicks, outTicks, durationTicks);

        Assert.Equal(inTicks, fittedIn);
        Assert.Equal(outTicks, fittedOut);
        Assert.Equal(1.0, stretch);
    }

    [Theory]
    [InlineData(0, 0, 1000)]
    [InlineData(-50, -50, 1000)]
    [InlineData(-100, 50, 1000)]
    public void Fit_ReturnsOriginalTicksAndUnitStretch_WhenTotalIsZeroOrNegative(long inTicks, long outTicks, long durationTicks)
    {
        (long fittedIn, long fittedOut, double stretch) = TitleMotion.Fit(inTicks, outTicks, durationTicks);

        Assert.Equal(inTicks, fittedIn);
        Assert.Equal(outTicks, fittedOut);
        Assert.Equal(1.0, stretch);
    }

    [Fact]
    public void Fit_ShrinksBothProportionally_AndConservesTotalDuration()
    {
        // total = 1000, duration = 500 -> stretch = 0.5
        (long fittedIn, long fittedOut, double stretch) = TitleMotion.Fit(600, 400, 500);

        Assert.Equal(300, fittedIn);
        Assert.Equal(200, fittedOut);
        Assert.Equal(0.5, stretch);
        Assert.Equal(500, fittedIn + fittedOut);
    }

    [Fact]
    public void Fit_HandlesZeroOutTicks_WhenShrinking()
    {
        // in = 1000, out = 0, duration = 400
        (long fittedIn, long fittedOut, double stretch) = TitleMotion.Fit(1000, 0, 400);

        Assert.Equal(400, fittedIn);
        Assert.Equal(0, fittedOut);
        Assert.Equal(0.4, stretch);
    }

    [Fact]
    public void Fit_HandlesZeroInTicks_WhenShrinking()
    {
        // in = 0, out = 1000, duration = 400
        (long fittedIn, long fittedOut, double stretch) = TitleMotion.Fit(0, 1000, 400);

        Assert.Equal(0, fittedIn);
        Assert.Equal(400, fittedOut);
        Assert.Equal(0.4, stretch);
    }

    [Theory]
    [InlineData(700, 300, 600)]
    [InlineData(999, 1, 500)]
    [InlineData(333, 667, 100)]
    public void Fit_ConservesDurationSum_AcrossArbitraryInputs(long inTicks, long outTicks, long durationTicks)
    {
        (long fittedIn, long fittedOut, double stretch) = TitleMotion.Fit(inTicks, outTicks, durationTicks);

        Assert.Equal(durationTicks, fittedIn + fittedOut);
        Assert.Equal((double)durationTicks / (inTicks + outTicks), stretch, 9);
        Assert.True(fittedIn <= durationTicks);
        Assert.True(fittedOut <= durationTicks);
    }

    [Fact]
    public void Compose_PreservesTransform_WithIdentityDelta()
    {
        (double x, double y, double scale, double rotation, double opacity) = TitleMotion.Compose(
            x: 10, y: 20, scale: 1.5, rotation: 45, opacity: 0.8,
            dx: 0, dy: 0, dScale: 1.0, dRotation: 0, dOpacity: 1.0);

        Assert.Equal(10, x);
        Assert.Equal(20, y);
        Assert.Equal(1.5, scale);
        Assert.Equal(45, rotation);
        Assert.Equal(0.8, opacity);
    }

    [Fact]
    public void Compose_AppliesTranslationAndScale_WithoutRotation()
    {
        (double x, double y, double scale, double rotation, double opacity) = TitleMotion.Compose(
            x: 100, y: 200, scale: 2.0, rotation: 0, opacity: 0.5,
            dx: 15, dy: -25, dScale: 1.5, dRotation: 10, dOpacity: 0.8);

        Assert.Equal(130.0, x, 6);
        Assert.Equal(150.0, y, 6);
        Assert.Equal(3.0, scale, 6);
        Assert.Equal(10.0, rotation, 6);
        Assert.Equal(0.4, opacity, 6);
    }

    [Theory]
    [InlineData(0, 10, 0, 10, 0)]
    [InlineData(90, 10, 0, 0, 10)]
    [InlineData(180, 10, 0, -10, 0)]
    [InlineData(270, 10, 0, 0, -10)]
    [InlineData(-90, 10, 0, 0, -10)]
    public void Compose_RotatesDeltaDisplacement_ByClipRotationAngle(
        double angle, double dx, double dy, double expectedX, double expectedY)
    {
        (double x, double y, _, _, _) = TitleMotion.Compose(
            x: 0, y: 0, scale: 1.0, rotation: angle, opacity: 1.0,
            dx: dx, dy: dy, dScale: 1.0, dRotation: 0, dOpacity: 1.0);

        Assert.Equal(expectedX, x, 5);
        Assert.Equal(expectedY, y, 5);
    }

    [Theory]
    [InlineData(0.8, 1.5, 1.0)]
    [InlineData(0.5, -0.2, 0.0)]
    [InlineData(0.6, 0.5, 0.3)]
    [InlineData(0.0, 2.0, 0.0)]
    public void Compose_ClampsResultingOpacity_BetweenZeroAndOne(double opacity, double dOpacity, double expected)
    {
        (_, _, _, _, double resultOpacity) = TitleMotion.Compose(
            x: 0, y: 0, scale: 1.0, rotation: 0, opacity: opacity,
            dx: 0, dy: 0, dScale: 1.0, dRotation: 0, dOpacity: dOpacity);

        Assert.Equal(expected, resultOpacity, 6);
    }
}
