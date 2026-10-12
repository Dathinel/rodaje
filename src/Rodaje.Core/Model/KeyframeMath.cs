namespace Rodaje.Core.Model;

/// <summary>
/// Curva de tiempo de un tramo entre keyframes, como <c>cubic-bezier</c> de CSS: va de (0, 0) a (1, 1) con dos puntos
/// de control. X es el avance del tiempo (de 0 a 1, así la curva es una función); Y es el avance del valor y puede salirse
/// de 0–1 (de <see cref="MinY"/> a <see cref="MaxY"/>) para pasarse y volver.
/// </summary>
public sealed record BezierEase(double X1, double Y1, double X2, double Y2)
{
    public const double MinY = -2;

    public const double MaxY = 3;

    /// <summary>La curva que equivale a «Suave» en la gráfica (la cúbica de <see cref="Interpolation.EaseInOut"/> se le parece).</summary>
    public static BezierEase Smooth { get; } = new(0.42, 0, 0.58, 1);

    public bool IsValid =>
        X1 is >= 0 and <= 1 && X2 is >= 0 and <= 1
        && Y1 is >= MinY and <= MaxY && Y2 is >= MinY and <= MaxY;

    /// <summary>La misma curva con los puntos dentro de los límites.</summary>
    public BezierEase Clamped() => new(
        Math.Clamp(Finite(X1, 0.42), 0, 1), Math.Clamp(Finite(Y1, 0), MinY, MaxY),
        Math.Clamp(Finite(X2, 0.58), 0, 1), Math.Clamp(Finite(Y2, 1), MinY, MaxY));

    private static double Finite(double value, double fallback) => double.IsFinite(value) ? value : fallback;
}

/// <summary>
/// Las fórmulas de los keyframes, compartidas por el núcleo (<see cref="Parameter.Evaluate"/>), el motor y la interfaz:
/// así la vista previa, la exportación y la gráfica del inspector dan los mismos números.
/// </summary>
public static class KeyframeMath
{
    /// <summary>
    /// Avance del valor para un avance del tiempo <paramref name="t"/> ∈ [0, 1] del tramo. Las cúbicas de siempre
    /// (entrada t³, salida 1 − (1 − t)³, suave en dos mitades), «rebote» y «elástico» de Robert Penner, y la Bézier de
    /// <paramref name="ease"/>. Bézier y elástico pueden salir de 0–1.
    /// </summary>
    public static double Ease(Interpolation interpolation, BezierEase? ease, double t)
    {
        switch (interpolation)
        {
            case Interpolation.Hold:
                return 0;
            case Interpolation.Linear:
                return t;
            case Interpolation.EaseIn:
                return t * t * t;
            case Interpolation.EaseOut:
            {
                double u = 1 - t;
                return 1 - (u * u * u);
            }

            case Interpolation.EaseInOut:
                return InOut(t);
            case Interpolation.Bezier:
                return ease is null ? InOut(t) : Bezier(ease, t);
            case Interpolation.Bounce:
                return Bounce(t);
            case Interpolation.Elastic:
                return Elastic(t);
            default:
                throw new ArgumentOutOfRangeException(nameof(interpolation), interpolation, "Interpolación desconocida.");
        }
    }

    /// <summary>
    /// El valor del tramo de <paramref name="from"/> a <paramref name="to"/> con el avance ya suavizado
    /// <paramref name="eased"/>. Sin tangentes es una recta; con alguna, una cúbica cuyos controles son el valor más la
    /// tangente de salida de <paramref name="from"/> y el valor más la de entrada de <paramref name="to"/> (la que falta
    /// vale un tercio del tramo, que deja recto ese lado).
    /// </summary>
    public static double Segment(Keyframe from, Keyframe to, double eased)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        return Segment(from.Value, from.SpatialOut, to.SpatialIn, to.Value, eased);
    }

    public static double Segment(double v0, double? out0, double? in1, double v1, double eased)
    {
        if (out0 is null && in1 is null)
        {
            return v0 + ((v1 - v0) * eased);
        }

        double third = (v1 - v0) / 3;
        return Cubic(v0, v0 + (out0 ?? third), v1 + (in1 ?? -third), v1, eased);
    }

    /// <summary>Cúbica de Bézier en 1D con los cuatro puntos de control.</summary>
    public static double Cubic(double p0, double p1, double p2, double p3, double u)
    {
        double v = 1 - u;
        return (v * v * v * p0) + (3 * v * v * u * p1) + (3 * v * u * u * p2) + (u * u * u * p3);
    }

    /// <summary>Derivada de <see cref="Cubic"/> respecto de <paramref name="u"/>.</summary>
    public static double CubicSlope(double p0, double p1, double p2, double p3, double u)
    {
        double v = 1 - u;
        return (3 * v * v * (p1 - p0)) + (6 * v * u * (p2 - p1)) + (3 * u * u * (p3 - p2));
    }

    /// <summary>Y de la curva de tiempo en el punto cuya X es <paramref name="t"/>.</summary>
    public static double Bezier(BezierEase ease, double t)
    {
        ArgumentNullException.ThrowIfNull(ease);
        if (t <= 0)
        {
            return 0;
        }

        if (t >= 1)
        {
            return 1;
        }

        double u = SolveX(ease.X1, ease.X2, t);
        return Cubic(0, ease.Y1, ease.Y2, 1, u);
    }

    /// <summary>
    /// El parámetro u ∈ [0, 1] con x(u) = <paramref name="t"/>: Newton desde t mientras la pendiente lo permita (para
    /// cuando el paso baja de 1e-13) y, si no, 64 pasos de bisección (u exacto a 2⁻⁶⁴). Se mira el paso y no el resto de
    /// x: donde la curva es plana un resto mínimo puede esconder un u muy distinto.
    /// </summary>
    internal static double SolveX(double x1, double x2, double t)
    {
        double u = t;
        for (int i = 0; i < 8; i++)
        {
            double slope = CubicSlope(0, x1, x2, 1, u);
            if (Math.Abs(slope) < 1e-6)
            {
                break;
            }

            double step = (Cubic(0, x1, x2, 1, u) - t) / slope;
            u -= step;
            if (u is < 0 or > 1)
            {
                break;
            }

            if (Math.Abs(step) < 1e-13)
            {
                return u;
            }
        }

        double lo = 0;
        double hi = 1;
        for (int i = 0; i < 64; i++)
        {
            double mid = (lo + hi) / 2;
            if (Cubic(0, x1, x2, 1, mid) < t)
            {
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }

        return (lo + hi) / 2;
    }

    /// <summary>«Rebote» (easeOutBounce de Penner): llega a 1 en t = 1/2,75 y rebota tres veces más bajo.</summary>
    public static double Bounce(double t)
    {
        const double N = 7.5625;
        const double D = 2.75;
        if (t <= 0)
        {
            return 0;
        }

        if (t >= 1)
        {
            return 1;
        }

        if (t < 1 / D)
        {
            return N * t * t;
        }

        if (t < 2 / D)
        {
            t -= 1.5 / D;
            return (N * t * t) + 0.75;
        }

        if (t < 2.5 / D)
        {
            t -= 2.25 / D;
            return (N * t * t) + 0.9375;
        }

        t -= 2.625 / D;
        return (N * t * t) + 0.984375;
    }

    /// <summary>«Elástico» (easeOutElastic de Penner): 2^(−10t)·sen((10t − 0,75)·2π/3) + 1; se pasa un 37 % y se asienta.</summary>
    public static double Elastic(double t)
    {
        if (t <= 0)
        {
            return 0;
        }

        if (t >= 1)
        {
            return 1;
        }

        return (Math.Pow(2, -10 * t) * Math.Sin(((t * 10) - 0.75) * (2 * Math.PI / 3))) + 1;
    }

    /// <summary>La interpolación puede pasarse de los valores de sus extremos (o la curva tiene tangentes).</summary>
    public static bool MayOvershoot(Keyframe from, Keyframe to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        return from.Interpolation is Interpolation.Elastic
            || (from.Interpolation == Interpolation.Bezier && from.Ease is { } e && (e.Y1 is < 0 or > 1 || e.Y2 is < 0 or > 1))
            || from.SpatialOut is not null || to.SpatialIn is not null;
    }

    private static double InOut(double t)
    {
        if (t < 0.5)
        {
            return 4 * t * t * t;
        }

        double u = (-2 * t) + 2;
        return 1 - (u * u * u / 2);
    }
}
