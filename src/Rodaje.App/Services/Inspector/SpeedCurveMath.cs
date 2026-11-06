using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Services.Inspector;

/// <summary>
/// Cuentas del editor de curvas de velocidad, sin WinUI. La altura es logarítmica: 0 es 0,1×, ½ es 1× y 1 es 10×. Las
/// posiciones van en diezmilésimas de la curva (<see cref="SpeedCurve.AtScale"/>), como sus puntos.
/// </summary>
public static class SpeedCurveMath
{
    public const int MinPoints = 2;

    /// <summary>La altura de una velocidad, de 0 (0,1×) a 1 (10×).</summary>
    public static double Level(double rate) =>
        Math.Clamp((Math.Log10(Math.Clamp(rate, 0.1, 10)) + 1) / 2, 0, 1);

    /// <summary>La velocidad en centésimas de una altura, dentro de 0,1×–10×.</summary>
    public static int HundredthsAt(double level) =>
        Math.Clamp((int)Math.Round(100 * Math.Pow(10, (2 * Math.Clamp(level, 0, 1)) - 1)), RetimeMath.MinHundredths, RetimeMath.MaxHundredths);

    /// <summary>La velocidad de la curva en <paramref name="at"/>; antes del primer punto y después del último sigue igual.</summary>
    public static double RateAt(IReadOnlyList<SpeedPoint> points, double at)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count == 0)
        {
            return 1;
        }

        if (at <= points[0].At)
        {
            return points[0].Hundredths / 100.0;
        }

        for (int k = 1; k < points.Count; k++)
        {
            SpeedPoint b = points[k];
            if (at <= b.At)
            {
                SpeedPoint a = points[k - 1];
                double t = (at - a.At) / (double)(b.At - a.At);
                return (a.Hundredths + ((b.Hundredths - a.Hundredths) * t)) / 100;
            }
        }

        return points[^1].Hundredths / 100.0;
    }

    /// <summary>
    /// La forma entre <paramref name="from"/> y <paramref name="to"/> como polilínea: x de 0 a 1 en ese tramo y la altura
    /// de cada punto. Lleva los puntos de la curva que caen dentro y <paramref name="subdivisions"/> trozos por tramo,
    /// porque una recta en velocidad se curva en la escala logarítmica.
    /// </summary>
    public static List<(double X, double Level)> Shape(IReadOnlyList<SpeedPoint> points, double from, double to, int subdivisions)
    {
        ArgumentNullException.ThrowIfNull(points);
        subdivisions = Math.Max(1, subdivisions);
        var knots = new List<double> { from };
        foreach (SpeedPoint p in points)
        {
            if (p.At > from && p.At < to)
            {
                knots.Add(p.At);
            }
        }

        knots.Add(to);
        double width = Math.Max(to - from, double.Epsilon);
        var shape = new List<(double, double)>((knots.Count * subdivisions) + 1);
        for (int k = 0; k < knots.Count - 1; k++)
        {
            for (int s = 0; s < subdivisions; s++)
            {
                double at = knots[k] + ((knots[k + 1] - knots[k]) * s / subdivisions);
                shape.Add(((at - from) / width, Level(RateAt(points, at))));
            }
        }

        shape.Add((1, Level(RateAt(points, to))));
        return shape;
    }

    /// <summary>
    /// El punto más cercano a (<paramref name="x"/>, <paramref name="y"/>), en píxeles de un área de
    /// <paramref name="width"/> × <paramref name="height"/> (0 arriba), a menos de <paramref name="radius"/>; si no, nulo.
    /// </summary>
    public static int? Hit(IReadOnlyList<SpeedPoint> points, double x, double y, double width, double height, double radius)
    {
        ArgumentNullException.ThrowIfNull(points);
        int? best = null;
        double bestDistance = radius * radius;
        for (int i = 0; i < points.Count; i++)
        {
            double dx = (points[i].At / (double)SpeedCurve.AtScale * width) - x;
            double dy = ((1 - Level(points[i].Hundredths / 100.0)) * height) - y;
            double distance = (dx * dx) + (dy * dy);
            if (distance <= bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }

        return best;
    }

    /// <summary>
    /// <paramref name="start"/> con el punto <paramref name="index"/> en (<paramref name="at"/>, <paramref name="hundredths"/>):
    /// la velocidad acotada a 0,1×–10× y la posición entre sus vecinos (orden estricto); el primero y el último no se
    /// mueven de sus extremos.
    /// </summary>
    public static List<SpeedPoint> Move(IReadOnlyList<SpeedPoint> start, int index, int at, int hundredths)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, start.Count);
        int position = index == 0 ? 0
            : index == start.Count - 1 ? SpeedCurve.AtScale
            : Math.Clamp(at, start[index - 1].At + 1, start[index + 1].At - 1);
        var moved = new List<SpeedPoint>(start);
        moved[index] = new SpeedPoint(position, Math.Clamp(hundredths, RetimeMath.MinHundredths, RetimeMath.MaxHundredths));
        return moved;
    }

    /// <summary>
    /// Un punto nuevo en <paramref name="at"/> sobre la curva (no cambia la forma hasta moverlo); nulo si ya hay
    /// <see cref="SpeedCurve.MaxPoints"/>, si cae en un extremo o encima de otro punto.
    /// </summary>
    public static List<SpeedPoint>? Add(IReadOnlyList<SpeedPoint> points, int at)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count >= SpeedCurve.MaxPoints || at <= 0 || at >= SpeedCurve.AtScale)
        {
            return null;
        }

        int index = 0;
        while (index < points.Count && points[index].At < at)
        {
            index++;
        }

        if (index < points.Count && points[index].At == at)
        {
            return null;
        }

        int hundredths = Math.Clamp((int)Math.Round(RateAt(points, at) * 100), RetimeMath.MinHundredths, RetimeMath.MaxHundredths);
        var added = new List<SpeedPoint>(points);
        added.Insert(index, new SpeedPoint(at, hundredths));
        return added;
    }

    /// <summary>Sin el punto <paramref name="index"/>; nulo si es un extremo o quedarían menos de <see cref="MinPoints"/>.</summary>
    public static List<SpeedPoint>? Remove(IReadOnlyList<SpeedPoint> points, int index)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count <= MinPoints || index <= 0 || index >= points.Count - 1)
        {
            return null;
        }

        var removed = new List<SpeedPoint>(points);
        removed.RemoveAt(index);
        return removed;
    }

    /// <summary>Una curva plana a <paramref name="rate"/> (acotada a 0,1×–10×), para empezar a editar a mano.</summary>
    public static List<SpeedPoint> Flat(double rate)
    {
        int hundredths = Math.Clamp((int)Math.Round(rate * 100), RetimeMath.MinHundredths, RetimeMath.MaxHundredths);
        return [new SpeedPoint(0, hundredths), new SpeedPoint(SpeedCurve.AtScale, hundredths)];
    }

    /// <summary>El tramo de la curva que cubre el clip, en diezmilésimas: todo salvo en un trozo de un clip dividido.</summary>
    public static (double From, double To) Window(Clip clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (clip.Speed is not { Curve: { } curve } speed || curve.Span <= Time.Zero)
        {
            return (0, SpeedCurve.AtScale);
        }

        double scale = SpeedCurve.AtScale / (double)curve.Span.Ticks;
        double from = Math.Clamp(speed.Offset.Ticks * scale, 0, SpeedCurve.AtScale);
        double to = Math.Clamp((speed.Offset + clip.Duration).Ticks * scale, from, SpeedCurve.AtScale);
        return (from, to);
    }

    /// <summary>Dónde cae <paramref name="time"/> de la línea de tiempo en la curva del clip; nulo si no tiene curva o cae fuera.</summary>
    public static double? AtOf(Clip clip, Time time)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (clip.Speed is not { Curve: { } curve } speed || curve.Span <= Time.Zero || time < clip.Start || time >= clip.End)
        {
            return null;
        }

        return Math.Clamp((speed.Offset + (time - clip.Start)).Ticks * (double)SpeedCurve.AtScale / curve.Span.Ticks, 0, SpeedCurve.AtScale);
    }

    /// <summary>El instante del clip (desde su inicio) que corresponde a <paramref name="at"/> de su curva.</summary>
    public static Time ClipTimeOf(Clip clip, double at)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (clip.Speed is not { Curve: { } curve } speed)
        {
            return Time.Zero;
        }

        return Time.FromTicks((long)Math.Round(at / SpeedCurve.AtScale * curve.Span.Ticks)) - speed.Offset;
    }

    /// <summary>La inversa de <see cref="ClipTimeOf"/>: dónde cae en la curva un instante del clip (desde su inicio), sin acotar.</summary>
    public static int AtOfClipTime(Clip clip, Time clipTime)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (clip.Speed is not { Curve: { Span.Ticks: > 0 } curve } speed)
        {
            return 0;
        }

        return (int)Math.Round((speed.Offset + clipTime).Ticks * (double)SpeedCurve.AtScale / curve.Span.Ticks);
    }
}
