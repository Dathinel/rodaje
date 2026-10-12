using Rodaje.Core.Timing;

namespace Rodaje.Core.Model;

/// <summary>
/// La trayectoria de la posición: entre dos keyframes de X e Y en los mismos instantes y con tangentes, la capa sigue la
/// curva de Bézier que forman (p0, p0 + salida, p1 + entrada, p1) a velocidad constante sobre la curva, y la
/// interpolación del keyframe de X reparte ese recorrido en el tiempo. Sin tangentes, X e Y se interpolan cada uno por su
/// lado (una recta).
/// </summary>
public static class MotionPath
{
    /// <summary>Tramos rectos con que se mide la curva.</summary>
    public const int Steps = 48;

    /// <summary>
    /// La posición en <paramref name="time"/> si cae dentro de un tramo con trayectoria; <c>false</c> si X e Y se evalúan
    /// cada uno por su lado.
    /// </summary>
    public static bool TryEvaluate(Parameter x, Parameter y, Time time, out double px, out double py)
    {
        ArgumentNullException.ThrowIfNull(x);
        ArgumentNullException.ThrowIfNull(y);
        px = 0;
        py = 0;
        List<Keyframe> xs = x.Keyframes;
        List<Keyframe> ys = y.Keyframes;
        if (xs.Count < 2 || ys.Count < 2 || time <= xs[0].Time || time >= xs[^1].Time)
        {
            return false;
        }

        int right = xs.FindIndex(k => k.Time > time);
        Keyframe xa = xs[right - 1];
        Keyframe xb = xs[right];
        int j = ys.FindIndex(k => k.Time == xa.Time);
        if (j < 0 || j + 1 >= ys.Count || ys[j + 1].Time != xb.Time)
        {
            return false;
        }

        Keyframe ya = ys[j];
        Keyframe yb = ys[j + 1];
        if (xa.SpatialOut is null && xb.SpatialIn is null && ya.SpatialOut is null && yb.SpatialIn is null)
        {
            return false;
        }

        double t = (double)(time - xa.Time).Ticks / (xb.Time - xa.Time).Ticks;
        double eased = KeyframeMath.Ease(xa.Interpolation, xa.Ease, t);
        (px, py) = Along(
            xa.Value, ya.Value,
            Control(xa.Value, xa.SpatialOut, xb.Value), Control(ya.Value, ya.SpatialOut, yb.Value),
            Control(xb.Value, xb.SpatialIn, xa.Value), Control(yb.Value, yb.SpatialIn, ya.Value),
            xb.Value, yb.Value, eased);
        return true;
    }

    /// <summary>
    /// El control de un extremo: el valor más su tangente o, si no tiene, un tercio del camino hacia el otro extremo
    /// (el lado queda recto).
    /// </summary>
    public static double Control(double value, double? tangent, double other) =>
        value + (tangent ?? ((other - value) / 3));

    /// <summary>
    /// El punto de la curva a la fracción <paramref name="fraction"/> de su largo (medido con <see cref="Steps"/> tramos).
    /// Fuera de 0–1 (un suavizado que se pasa) sigue en línea recta por la tangente del extremo.
    /// </summary>
    public static (double X, double Y) Along(double x0, double y0, double cx0, double cy0, double cx1, double cy1, double x1, double y1,
        double fraction)
    {
        Span<double> lengths = stackalloc double[Steps + 1];
        lengths[0] = 0;
        double previousX = x0;
        double previousY = y0;
        for (int i = 1; i <= Steps; i++)
        {
            double u = (double)i / Steps;
            double nx = KeyframeMath.Cubic(x0, cx0, cx1, x1, u);
            double ny = KeyframeMath.Cubic(y0, cy0, cy1, y1, u);
            lengths[i] = lengths[i - 1] + Math.Sqrt(((nx - previousX) * (nx - previousX)) + ((ny - previousY) * (ny - previousY)));
            previousX = nx;
            previousY = ny;
        }

        double total = lengths[Steps];
        if (!(total > 1e-9))
        {
            return (x0 + ((x1 - x0) * fraction), y0 + ((y1 - y0) * fraction));
        }

        if (fraction < 0 || fraction > 1)
        {
            bool start = fraction < 0;
            double u = start ? 0 : 1;
            double dx = KeyframeMath.CubicSlope(x0, cx0, cx1, x1, u);
            double dy = KeyframeMath.CubicSlope(y0, cy0, cy1, y1, u);
            double norm = Math.Sqrt((dx * dx) + (dy * dy));
            if (norm < 1e-12)
            {
                (dx, dy, norm) = (x1 - x0, y1 - y0, total);
            }

            double distance = (start ? fraction : fraction - 1) * total / norm;
            return start ? (x0 + (dx * distance), y0 + (dy * distance)) : (x1 + (dx * distance), y1 + (dy * distance));
        }

        double target = fraction * total;
        int lo = 0;
        int hi = Steps;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) >>> 1;
            if (lengths[mid] <= target)
            {
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }

        double span = lengths[hi] - lengths[lo];
        double local = span > 0 ? (target - lengths[lo]) / span : 0;
        double at = (lo + local) / Steps;
        return (KeyframeMath.Cubic(x0, cx0, cx1, x1, at), KeyframeMath.Cubic(y0, cy0, cy1, y1, at));
    }
}
