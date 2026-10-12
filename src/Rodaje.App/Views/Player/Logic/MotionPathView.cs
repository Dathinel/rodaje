using System.Numerics;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using T = Rodaje.Core.Model.BuiltInEffects.TransformParameters;

namespace Rodaje.App.Views.Player;

/// <summary>
/// Un keyframe de posición en el visor (en píxeles de la secuencia): el punto y sus dos manejadores Bézier. Los
/// manejadores que el keyframe no fija están donde dejan el lado recto (un tercio hacia el vecino).
/// </summary>
public sealed record PathKey(Time Time, Vector2 Point, Vector2? In, Vector2? Out);

/// <summary>
/// La trayectoria de la posición para dibujarla en el visor, sin WinUI: muestreada con las mismas fórmulas del motor
/// (<see cref="MotionPath"/>), con el ancla sumada (lo que recorre es el punto de anclaje) y los keyframes que tienen X e
/// Y en el mismo instante, que son los que llevan manejadores.
/// </summary>
public static class MotionPathView
{
    public const int SamplesPerSegment = 24;

    /// <summary>X o Y tienen al menos dos keyframes.</summary>
    public static bool IsAnimated(EffectInstance? transform) =>
        transform is not null && (Keys(transform, T.X).Count >= 2 || Keys(transform, T.Y).Count >= 2);

    /// <summary>El recorrido en píxeles de la secuencia, de su primer keyframe al último.</summary>
    public static IReadOnlyList<Vector2> Sample(EffectInstance transform, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(transform);
        List<Time> times = [.. Keys(transform, T.X).Concat(Keys(transform, T.Y)).Select(k => k.Time).Distinct().Order()];
        if (times.Count < 2)
        {
            return [];
        }

        var points = new List<Vector2>(((times.Count - 1) * SamplesPerSegment) + 1);
        for (int k = 0; k + 1 < times.Count; k++)
        {
            long from = times[k].Ticks;
            long span = times[k + 1].Ticks - from;
            for (int i = k == 0 ? 0 : 1; i <= SamplesPerSegment; i++)
            {
                points.Add(At(transform, width, height, Time.FromTicks(from + (span * i / SamplesPerSegment))));
            }
        }

        return points;
    }

    /// <summary>Dónde está el punto de anclaje en <paramref name="time"/> (tiempo de los keyframes), en píxeles de la secuencia.</summary>
    public static Vector2 At(EffectInstance transform, int width, int height, Time time)
    {
        ArgumentNullException.ThrowIfNull(transform);
        Parameter x = Param(transform, T.X);
        Parameter y = Param(transform, T.Y);
        (double px, double py) = MotionPath.TryEvaluate(x, y, time, out double ex, out double ey) ? (ex, ey) : (x.Evaluate(time), y.Evaluate(time));
        double ax = Param(transform, T.AnchorX).Evaluate(time);
        double ay = Param(transform, T.AnchorY).Evaluate(time);
        return new Vector2((float)((width / 2.0) + px + ax), (float)((height / 2.0) + py + ay));
    }

    /// <summary>Los keyframes con X e Y en el mismo instante, con sus manejadores.</summary>
    public static IReadOnlyList<PathKey> Handles(EffectInstance transform, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(transform);
        List<Keyframe> xs = Keys(transform, T.X);
        List<Keyframe> ys = Keys(transform, T.Y);
        var keys = new List<PathKey>();
        for (int i = 0; i < xs.Count; i++)
        {
            int j = ys.FindIndex(k => k.Time == xs[i].Time);
            if (j < 0)
            {
                continue;
            }

            Vector2 point = At(transform, width, height, xs[i].Time);
            Vector2? incoming = null;
            Vector2? outgoing = null;
            if (i > 0 && j > 0 && ys[j - 1].Time == xs[i - 1].Time)
            {
                incoming = point + new Vector2(
                    (float)(MotionPath.Control(xs[i].Value, xs[i].SpatialIn, xs[i - 1].Value) - xs[i].Value),
                    (float)(MotionPath.Control(ys[j].Value, ys[j].SpatialIn, ys[j - 1].Value) - ys[j].Value));
            }

            if (i + 1 < xs.Count && j + 1 < ys.Count && ys[j + 1].Time == xs[i + 1].Time)
            {
                outgoing = point + new Vector2(
                    (float)(MotionPath.Control(xs[i].Value, xs[i].SpatialOut, xs[i + 1].Value) - xs[i].Value),
                    (float)(MotionPath.Control(ys[j].Value, ys[j].SpatialOut, ys[j + 1].Value) - ys[j].Value));
            }

            keys.Add(new PathKey(xs[i].Time, point, incoming, outgoing));
        }

        return keys;
    }

    /// <summary>
    /// Las tangentes al soltar un manejador en <paramref name="handle"/>: la del lado arrastrado va hasta el puntero y, si
    /// <paramref name="mirror"/>, la del otro lado se alinea en sentido opuesto conservando su largo (curva suave).
    /// </summary>
    public static ((double X, double Y)? In, (double X, double Y)? Out) Tangents(PathKey key, bool outgoing, Vector2 handle, bool mirror)
    {
        ArgumentNullException.ThrowIfNull(key);
        Vector2 dragged = handle - key.Point;
        Vector2? other = (outgoing ? key.In : key.Out) is { } h ? h - key.Point : null;
        if (mirror && other is { } o && dragged.LengthSquared() > 1e-6f)
        {
            other = -Vector2.Normalize(dragged) * o.Length();
        }

        (double, double)? Pair(Vector2? v) => v is { } p ? (Math.Round(p.X, 2), Math.Round(p.Y, 2)) : null;
        return outgoing ? (Pair(other), Pair(dragged)) : (Pair(dragged), Pair(other));
    }

    private static List<Keyframe> Keys(EffectInstance transform, string name) =>
        transform.Parameters.TryGetValue(name, out Parameter? p) ? p.Keyframes : [];

    private static Parameter Param(EffectInstance transform, string name) =>
        transform.Parameters.TryGetValue(name, out Parameter? p) ? p : new Parameter { Value = T.DefaultValue(name) };
}
