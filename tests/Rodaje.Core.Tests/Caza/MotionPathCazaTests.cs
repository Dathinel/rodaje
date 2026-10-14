using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Xunit;

namespace Rodaje.Core.Tests.Caza;

public class MotionPathCazaTests
{
    [Fact]
    public void Along_StartAndEndFractions_MatchCurveEndPoints()
    {
        var start = MotionPath.Along(10, 20, 10, 80, 70, 200, 100, 200, 0.0);
        var end = MotionPath.Along(10, 20, 10, 80, 70, 200, 100, 200, 1.0);

        Assert.Equal(10.0, start.X, 3);
        Assert.Equal(20.0, start.Y, 3);
        Assert.Equal(100.0, end.X, 3);
        Assert.Equal(200.0, end.Y, 3);
    }

    [Fact]
    public void Along_NonlinearParameterization_MaintainsConstantSpeedAlongCurve()
    {
        var midpoint = MotionPath.Along(0, 0, 0, 0, 0, 0, 100, 0, 0.5);

        Assert.Equal(50.0, midpoint.X, 0);
        Assert.Equal(0.0, midpoint.Y, 3);
    }

    [Fact]
    public void Along_CurvedPath_EqualFractionStepsCoverEqualArcLengths()
    {
        double x0 = 0, y0 = 0;
        double cx0 = 0, cy0 = 100;
        double cx1 = 100, cy1 = 100;
        double x1 = 100, y1 = 0;

        var p0 = MotionPath.Along(x0, y0, cx0, cy0, cx1, cy1, x1, y1, 0.0);
        var pMid = MotionPath.Along(x0, y0, cx0, cy0, cx1, cy1, x1, y1, 0.5);
        var p1 = MotionPath.Along(x0, y0, cx0, cy0, cx1, cy1, x1, y1, 1.0);

        double dist1 = Math.Sqrt(Math.Pow(pMid.X - p0.X, 2) + Math.Pow(pMid.Y - p0.Y, 2));
        double dist2 = Math.Sqrt(Math.Pow(p1.X - pMid.X, 2) + Math.Pow(p1.Y - pMid.Y, 2));

        Assert.Equal(dist1, dist2, 1);
    }

    [Theory]
    [InlineData(0.25, 25.0, 50.0)]
    [InlineData(0.50, 50.0, 100.0)]
    [InlineData(0.75, 75.0, 150.0)]
    public void Along_StraightPath_PositionIsProportionalToFraction(double fraction, double expectedX, double expectedY)
    {
        double x0 = 0, y0 = 0;
        double x1 = 100, y1 = 200;
        double cx0 = x0 + (x1 - x0) / 3.0;
        double cy0 = y0 + (y1 - y0) / 3.0;
        double cx1 = x1 - (x1 - x0) / 3.0;
        double cy1 = y1 - (y1 - y0) / 3.0;

        var pos = MotionPath.Along(x0, y0, cx0, cy0, cx1, cy1, x1, y1, fraction);

        Assert.Equal(expectedX, pos.X, 1);
        Assert.Equal(expectedY, pos.Y, 1);
    }

    [Fact]
    public void TryEvaluate_LinearKeyframes_EvaluatesPositionAlongCurveAtConstantSpeed()
    {
        var paramX = new Parameter
        {
            Keyframes =
            [
                new Keyframe(Time.Zero, 0) { SpatialOut = 0.0 },
                new Keyframe(Time.FromSeconds(4), 100) { SpatialIn = 0.0 },
            ],
        };
        var paramY = new Parameter
        {
            Keyframes =
            [
                new Keyframe(Time.Zero, 0) { SpatialOut = 50.0 },
                new Keyframe(Time.FromSeconds(4), 0) { SpatialIn = 50.0 },
            ],
        };

        bool evaluated = MotionPath.TryEvaluate(paramX, paramY, Time.FromSeconds(2), out double px, out double py);

        Assert.True(evaluated);
        Assert.Equal(50.0, px, 1);
        Assert.True(py > 0);
    }

    [Fact]
    public void TryEvaluate_TimeSteps_AdvanceAlongCurveAtConstantSpeed()
    {
        var paramX = new Parameter
        {
            Keyframes =
            [
                new Keyframe(Time.Zero, 0) { SpatialOut = 0.0 },
                new Keyframe(Time.FromSeconds(4), 100) { SpatialIn = 0.0 },
            ],
        };
        var paramY = new Parameter
        {
            Keyframes =
            [
                new Keyframe(Time.Zero, 0) { SpatialOut = 60.0 },
                new Keyframe(Time.FromSeconds(4), 0) { SpatialIn = 60.0 },
            ],
        };

        bool okQuarter = MotionPath.TryEvaluate(paramX, paramY, Time.FromSeconds(1), out double xQuarter, out double yQuarter);
        bool okThreeQuarters = MotionPath.TryEvaluate(paramX, paramY, Time.FromSeconds(3), out double xThreeQuarters, out double yThreeQuarters);

        Assert.True(okQuarter);
        Assert.True(okThreeQuarters);
        Assert.Equal(100.0 - xQuarter, xThreeQuarters, 1);
        Assert.Equal(yQuarter, yThreeQuarters, 1);
    }
}
