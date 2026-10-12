using CsCheck;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Tests.Model;

/// <summary>Curvas de tiempo y trayectorias contra referencias propias (bisección y medida fina del largo).</summary>
public sealed class KeyframeMathTests
{
    /// <summary>Referencia: bisección de 200 pasos sobre x(u), sin Newton.</summary>
    private static double ReferenceBezier(BezierEase e, double t)
    {
        double lo = 0;
        double hi = 1;
        for (int i = 0; i < 200; i++)
        {
            double mid = (lo + hi) / 2;
            double x = (3 * (1 - mid) * (1 - mid) * mid * e.X1) + (3 * (1 - mid) * mid * mid * e.X2) + (mid * mid * mid);
            if (x < t)
            {
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }

        double u = (lo + hi) / 2;
        return (3 * (1 - u) * (1 - u) * u * e.Y1) + (3 * (1 - u) * u * u * e.Y2) + (u * u * u);
    }

    [Fact]
    public void Bezier_MatchesBisectionReference_ForAnyControlPoints()
    {
        // A menos de 1e-9 de los bordes, una curva que arranca o llega plana (x1 = 0 o x2 = 1) amplifica el redondeo de t
        // por la raíz cúbica: cualquier u con x(u) = t en doble precisión vale, y la referencia elige otro.
        Gen.Select(Gen.Double[0, 1], Gen.Double[-2, 3], Gen.Double[0, 1], Gen.Double[-2, 3], Gen.Double[1e-9, 1 - 1e-9]).Sample((x1, y1, x2, y2, t) =>
        {
            var ease = new BezierEase(x1, y1, x2, y2);
            double expected = ReferenceBezier(ease, t);
            double actual = KeyframeMath.Bezier(ease, t);
            Assert.True(Math.Abs(expected - actual) < 1e-7, $"{expected} frente a {actual}");
        }, iter: 20_000);
    }

    [Fact]
    public void Bezier_KnownCurves()
    {
        // Lineal y simétricas: (0,0,1,1) es la identidad; una curva simétrica pasa por (0,5; 0,5).
        Assert.Equal(0.3, KeyframeMath.Bezier(new BezierEase(0, 0, 1, 1), 0.3), 12);
        Assert.Equal(0.5, KeyframeMath.Bezier(BezierEase.Smooth, 0.5), 12);
        Assert.Equal(0.5, KeyframeMath.Bezier(new BezierEase(0.9, 0.1, 0.1, 0.9), 0.5), 12);

        // «ease» de CSS (0,25, 0,1, 0,25, 1) en t = 0,5: 0,8024033877 (valor de referencia de los navegadores).
        Assert.Equal(0.8024033877, KeyframeMath.Bezier(new BezierEase(0.25, 0.1, 0.25, 1), 0.5), 8);
        Assert.Equal(0, KeyframeMath.Bezier(BezierEase.Smooth, 0));
        Assert.Equal(1, KeyframeMath.Bezier(BezierEase.Smooth, 1));
    }

    [Fact]
    public void BounceAndElastic_KnownValues()
    {
        Assert.Equal(0, KeyframeMath.Bounce(0));
        Assert.Equal(1, KeyframeMath.Bounce(1));
        Assert.Equal(1, KeyframeMath.Bounce(1 / 2.75), 12);
        Assert.Equal(0.765625, KeyframeMath.Bounce(0.5), 12);
        Assert.Equal(1.25, KeyframeMath.Elastic(0.1), 12);
        Assert.Equal(0, KeyframeMath.Elastic(0));
        Assert.Equal(1, KeyframeMath.Elastic(1));

        // Continuas en los cortes de los botes y dentro de 0–1.
        foreach (double edge in new[] { 1 / 2.75, 2 / 2.75, 2.5 / 2.75 })
        {
            Assert.Equal(KeyframeMath.Bounce(edge - 1e-9), KeyframeMath.Bounce(edge + 1e-9), 6);
        }

        Gen.Double[0, 1].Sample(t => Assert.InRange(KeyframeMath.Bounce(t), 0, 1));
        Assert.True(Enumerable.Range(1, 99).Max(i => KeyframeMath.Elastic(i / 100.0)) > 1.3);
    }

    [Theory]
    [InlineData(Interpolation.Bezier)]
    [InlineData(Interpolation.Bounce)]
    [InlineData(Interpolation.Elastic)]
    public void NewEasings_StartAtZeroAndEndAtOne(Interpolation interpolation)
    {
        Assert.Equal(0, KeyframeMath.Ease(interpolation, new BezierEase(0.2, 1.5, 0.4, -0.5), 0), 12);
        Assert.Equal(1, KeyframeMath.Ease(interpolation, new BezierEase(0.2, 1.5, 0.4, -0.5), 1), 12);
    }

    [Fact]
    public void BezierWithoutPoints_IsSmooth()
    {
        Gen.Double[0, 1].Sample(t => Assert.Equal(KeyframeMath.Ease(Interpolation.EaseInOut, null, t), KeyframeMath.Ease(Interpolation.Bezier, null, t)));
    }

    [Fact]
    public void Segment_WithoutTangents_OrWithThirds_IsAStraightLine()
    {
        Gen.Select(Gen.Double[-1000, 1000], Gen.Double[-1000, 1000], Gen.Double[0, 1]).Sample((a, b, u) =>
        {
            double straight = a + ((b - a) * u);
            Assert.Equal(straight, KeyframeMath.Segment(a, null, null, b, u), 9);
            Assert.Equal(straight, KeyframeMath.Segment(a, (b - a) / 3, -(b - a) / 3, b, u), 9);
        });
    }

    [Fact]
    public void Parameter_UsesTheBezierOfItsLeftKeyframe()
    {
        var ease = new BezierEase(0.1, 0.9, 0.2, 1);
        var p = new Parameter
        {
            Keyframes = [new Keyframe(Time.Zero, 10, Interpolation.Bezier) { Ease = ease }, new Keyframe(Time.FromSeconds(2), 30)],
        };
        Assert.Equal(10 + (20 * ReferenceBezier(ease, 0.25)), p.Evaluate(Time.FromSeconds(0.5)), 9);
    }

    [Fact]
    public void MotionPath_StraightControls_IsTheLineAtConstantSpeed()
    {
        for (int i = 0; i <= 10; i++)
        {
            double f = i / 10.0;
            (double x, double y) = MotionPath.Along(0, 0, 100 / 3.0, 0, 200 / 3.0, 0, 100, 0, f);
            Assert.Equal(100 * f, x, 6);
            Assert.Equal(0, y, 9);

            // Con los controles juntos a los extremos la curva corre distinto, pero el largo la deja pareja (< 0,01 px).
            (double bunched, _) = MotionPath.Along(0, 0, 10, 0, 90, 0, 100, 0, f);
            Assert.True(Math.Abs(bunched - (100 * f)) < 0.01, $"{bunched} en {f}");
        }
    }

    [Fact]
    public void MotionPath_CurvedPath_IsWithinHalfAPixelOfAFineArcLengthReference()
    {
        // Un arco en S, con los controles lejos: la velocidad sobre la curva es pareja y el punto cae donde dice el largo.
        (double X0, double Y0, double CX0, double CY0, double CX1, double CY1, double X1, double Y1) c = (0, 0, 400, -300, -100, 500, 600, 200);
        var reference = new List<(double U, double L, double X, double Y)>();
        double length = 0;
        double px = c.X0;
        double py = c.Y0;
        const int Fine = 100_000;
        for (int i = 0; i <= Fine; i++)
        {
            double u = (double)i / Fine;
            double x = KeyframeMath.Cubic(c.X0, c.CX0, c.CX1, c.X1, u);
            double y = KeyframeMath.Cubic(c.Y0, c.CY0, c.CY1, c.Y1, u);
            length += Math.Sqrt(((x - px) * (x - px)) + ((y - py) * (y - py)));
            reference.Add((u, length, x, y));
            (px, py) = (x, y);
        }

        double worst = 0;
        for (int k = 0; k <= 50; k++)
        {
            double f = k / 50.0;
            (double U, double L, double X, double Y) target = reference.First(r => r.L >= f * length);
            (double x, double y) = MotionPath.Along(c.X0, c.Y0, c.CX0, c.CY0, c.CX1, c.CY1, c.X1, c.Y1, f);
            worst = Math.Max(worst, Math.Sqrt(((x - target.X) * (x - target.X)) + ((y - target.Y) * (y - target.Y))));
        }

        Assert.True(worst < 0.5, $"Error máximo {worst:0.###} px");
    }

    [Fact]
    public void MotionPath_OvershootContinuesAlongTheEndTangent()
    {
        (double x, double y) = MotionPath.Along(0, 0, 33, 0, 66, 0, 100, 0, 1.2);
        Assert.Equal(120, x, 6);
        Assert.Equal(0, y, 9);
    }

    [Fact]
    public void TryEvaluate_NeedsTangentsAndMatchingKeyframes()
    {
        Time a = Time.Zero;
        Time b = Time.FromSeconds(1);
        var x = new Parameter { Keyframes = [new Keyframe(a, 0) { SpatialOut = 0 }, new Keyframe(b, 100) { SpatialIn = 0 }] };
        var y = new Parameter { Keyframes = [new Keyframe(a, 0) { SpatialOut = -200 }, new Keyframe(b, 0) { SpatialIn = -200 }] };
        Assert.True(MotionPath.TryEvaluate(x, y, Time.FromSeconds(0.5), out double px, out double py));
        Assert.Equal(50, px, 6);
        Assert.Equal(-150, py, 6);

        var plain = new Parameter { Keyframes = [new Keyframe(a, 0), new Keyframe(b, 100)] };
        Assert.False(MotionPath.TryEvaluate(plain, plain, Time.FromSeconds(0.5), out _, out _));
        var shifted = new Parameter { Keyframes = [new Keyframe(a, 0) { SpatialOut = 5 }, new Keyframe(Time.FromSeconds(2), 100)] };
        Assert.False(MotionPath.TryEvaluate(x, shifted, Time.FromSeconds(0.5), out _, out _));
    }
}
