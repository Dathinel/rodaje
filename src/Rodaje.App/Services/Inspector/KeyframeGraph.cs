using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Services.Inspector;

/// <summary>Qué muestra el editor de gráficas: el valor del parámetro o su velocidad (cuánto cambia por segundo).</summary>
public enum GraphMode
{
    Value,
    Speed,
}

/// <summary>Un punto de la gráfica: segundos desde el primer keyframe y el valor (o la velocidad) en unidades del modelo.</summary>
public readonly record struct GraphPoint(double Seconds, double Value);

/// <summary>
/// La cuenta del editor de gráficas, sin WinUI: muestrea la curva de un parámetro (con las mismas fórmulas del motor),
/// calcula su velocidad, y lleva los manejadores de un tramo Bézier entre el espacio del tramo (0–1) y la gráfica.
/// </summary>
public static class KeyframeGraph
{
    /// <summary>Muestras por tramo entre dos keyframes.</summary>
    public const int SamplesPerSegment = 48;

    /// <summary>La curva entre el primer y el último keyframe (en segundos desde el primero), en valor o en velocidad.</summary>
    /// <param name="partner">Para X o Y de la posición, el otro eje: la trayectoria se evalúa junta, como en el motor.</param>
    /// <param name="partnerIsY">Si <paramref name="partner"/> es Y (el parámetro dibujado es X).</param>
    public static IReadOnlyList<GraphPoint> Sample(Parameter parameter, GraphMode mode, Parameter? partner = null, bool partnerIsY = true)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        List<Keyframe> keys = parameter.Keyframes;
        if (keys.Count < 2)
        {
            return [];
        }

        Time origin = keys[0].Time;
        var points = new List<GraphPoint>(((keys.Count - 1) * SamplesPerSegment) + 1);
        for (int k = 0; k + 1 < keys.Count; k++)
        {
            long from = keys[k].Time.Ticks;
            long span = keys[k + 1].Time.Ticks - from;
            for (int i = k == 0 ? 0 : 1; i <= SamplesPerSegment; i++)
            {
                // Al final del tramo se mira justo antes del keyframe siguiente: «mantener» salta ahí.
                long at = from + (span * i / SamplesPerSegment) - (i == SamplesPerSegment ? 1 : 0);
                Time time = Time.FromTicks(at);
                double seconds = (time - origin).ToSeconds();
                points.Add(new GraphPoint(seconds, mode == GraphMode.Value
                    ? ValueAt(parameter, partner, partnerIsY, time)
                    : SpeedAt(parameter, partner, partnerIsY, time, Math.Max(1, span / (4 * SamplesPerSegment)))));
            }
        }

        return points;
    }

    /// <summary>El valor en <paramref name="time"/>: sobre la trayectoria si es X o Y de una posición con tangentes.</summary>
    public static double ValueAt(Parameter parameter, Parameter? partner, bool partnerIsY, Time time)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        if (partner is not null)
        {
            (Parameter x, Parameter y) = partnerIsY ? (parameter, partner) : (partner, parameter);
            if (MotionPath.TryEvaluate(x, y, time, out double px, out double py))
            {
                return partnerIsY ? px : py;
            }
        }

        return parameter.Evaluate(time);
    }

    /// <summary>Cuánto cambia por segundo (diferencia centrada de ±<paramref name="halfTicks"/>); en la posición, la rapidez sobre la trayectoria.</summary>
    public static double SpeedAt(Parameter parameter, Parameter? partner, bool partnerIsY, Time time, long halfTicks)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        Time before = time - Time.FromTicks(halfTicks);
        Time after = time + Time.FromTicks(halfTicks);
        double seconds = (after - before).ToSeconds();
        if (partner is not null)
        {
            (Parameter x, Parameter y) = partnerIsY ? (parameter, partner) : (partner, parameter);
            double dx = ValueAt(x, y, true, after) - ValueAt(x, y, true, before);
            double dy = ValueAt(y, x, false, after) - ValueAt(y, x, false, before);
            return Math.Sqrt((dx * dx) + (dy * dy)) / seconds;
        }

        return (parameter.Evaluate(after) - parameter.Evaluate(before)) / seconds;
    }

    /// <summary>El rango de valores que se ve, con un margen del 10 % y al menos <paramref name="minimumSpan"/>.</summary>
    public static (double Low, double High) Range(IReadOnlyList<GraphPoint> points, double minimumSpan)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count == 0)
        {
            return (0, Math.Max(minimumSpan, 1));
        }

        double low = points.Min(p => p.Value);
        double high = points.Max(p => p.Value);
        double span = Math.Max(high - low, minimumSpan);
        double middle = (low + high) / 2;
        return (middle - (span * 0.6), middle + (span * 0.6));
    }

    /// <summary>
    /// Los dos manejadores del tramo que sale de <paramref name="from"/>, en (segundos, valor): el de salida en
    /// (x1, y1) y el de entrada en (x2, y2) de la caja del tramo. Sin curva Bézier, los de «Suave».
    /// </summary>
    public static (GraphPoint Out, GraphPoint In) Handles(Keyframe from, Keyframe to, Time origin)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        BezierEase ease = EaseOf(from);
        double t0 = (from.Time - origin).ToSeconds();
        double span = (to.Time - from.Time).ToSeconds();
        double dv = to.Value - from.Value;
        return (new GraphPoint(t0 + (ease.X1 * span), from.Value + (ease.Y1 * dv)), new GraphPoint(t0 + (ease.X2 * span), from.Value + (ease.Y2 * dv)));
    }

    /// <summary>
    /// La curva Bézier del tramo con uno de sus manejadores llevado a <paramref name="point"/> (segundos, valor). En un tramo
    /// plano (los dos keyframes con el mismo valor) la altura del manejador no se puede expresar y se deja como estaba.
    /// </summary>
    public static BezierEase Drag(Keyframe from, Keyframe to, Time origin, bool outgoing, GraphPoint point)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        BezierEase ease = EaseOf(from);
        double t0 = (from.Time - origin).ToSeconds();
        double span = Math.Max(1e-9, (to.Time - from.Time).ToSeconds());
        double dv = to.Value - from.Value;
        double x = Math.Clamp((point.Seconds - t0) / span, 0, 1);
        double? y = Math.Abs(dv) > 1e-12 ? Math.Clamp((point.Value - from.Value) / dv, BezierEase.MinY, BezierEase.MaxY) : null;
        return outgoing
            ? ease with { X1 = Math.Round(x, 4), Y1 = Math.Round(y ?? ease.Y1, 4) }
            : ease with { X2 = Math.Round(x, 4), Y2 = Math.Round(y ?? ease.Y2, 4) };
    }

    /// <summary>La curva de tiempo del keyframe como Bézier: la suya o, para las fijas, la que más se le parece.</summary>
    public static BezierEase EaseOf(Keyframe key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return key.Interpolation switch
        {
            Interpolation.Bezier => key.Ease ?? BezierEase.Smooth,
            Interpolation.Linear or Interpolation.Hold => new BezierEase(1 / 3.0, 1 / 3.0, 2 / 3.0, 2 / 3.0),
            Interpolation.EaseIn => new BezierEase(0.55, 0.055, 0.675, 0.19),
            Interpolation.EaseOut => new BezierEase(0.215, 0.61, 0.355, 1),
            _ => BezierEase.Smooth,
        };
    }
}
