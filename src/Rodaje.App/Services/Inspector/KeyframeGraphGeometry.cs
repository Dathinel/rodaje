using Rodaje.App.Services.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.App.Views.Timeline;

namespace Rodaje.App.Services.Inspector;

/// <summary>Transformación e interacción de la gráfica en tiempo de secuencia, sin tipos de interfaz.</summary>
public sealed record KeyframeGraphGeometry(Clip Clip, double PixelsPerSecond, double ScrollX,
    double Width, double Height, double Low, double High)
{
    public const double Padding = 10;

    public IReadOnlyList<RulerTick> TimeTicks(Rational frameRate) => RulerScale.Ticks(new TimelineViewport
    {
        PixelsPerSecond = PixelsPerSecond,
        ScrollX = ScrollX,
        Width = Math.Max(1, Width - (2 * Padding)),
    }, RulerScale.ChooseStep(PixelsPerSecond, frameRate));

    public static ViewFrame FitTime(Clip clip, double width)
    {
        ArgumentNullException.ThrowIfNull(clip);
        double scale = Math.Clamp(Math.Max(1, width - (2 * Padding)) / Math.Max(1e-6, clip.Duration.ToSeconds()),
            TimelineState.MinPixelsPerSecond, TimelineState.MaxPixelsPerSecond);
        return new ViewFrame(scale, clip.Start.ToSeconds() * scale);
    }

    public GraphPoint Pixel(Time time, double value) => new(
        Padding + (EffectCommands.TimelineOfKeyframe(Clip, time).ToSeconds() * PixelsPerSecond) - ScrollX,
        Padding + ((High - value) / (High - Low) * Math.Max(1, Height - (2 * Padding))));

    public Time TimeAt(double x, Rational? frameRate = null)
    {
        Time timeline = Time.FromSeconds(Math.Max(0, (x - Padding + ScrollX) / PixelsPerSecond));
        if (frameRate is { } rate)
        {
            timeline = Time.FromFrames(timeline.ToFrames(rate), rate);
        }

        return EffectCommands.KeyframeTime(Clip, Time.Max(Clip.Start, Time.Min(Clip.End, timeline)));
    }

    public double ValueAt(double y) => High - ((y - Padding) / Math.Max(1, Height - (2 * Padding)) * (High - Low));

    public GraphPoint FromPixel(GraphPoint pixel, Time origin) => new((TimeAt(pixel.Seconds) - origin).ToSeconds(), ValueAt(pixel.Value));

    public GraphPoint HandlePixel(GraphPoint handle, Time origin) => Pixel(origin + Time.FromSeconds(handle.Seconds), handle.Value);

    public static bool Near(GraphPoint point, GraphPoint target, double radius) =>
        Math.Pow(point.Seconds - target.Seconds, 2) + Math.Pow(point.Value - target.Value, 2) <= radius * radius;

    /// <summary>Qué hay bajo <paramref name="pixel"/>: un manejador (0 salida, 1 entrada) del tramo Bézier que sale de una clave seleccionada, o una clave (−1).</summary>
    public (int Key, int Handle)? Hit(IReadOnlyList<Keyframe> keys, IReadOnlySet<Time> selection, GraphPoint pixel, double radius)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(selection);
        for (int selected = 0; selected + 1 < keys.Count; selected++)
        {
            if (!selection.Contains(keys[selected].Time) || keys[selected].Interpolation != Interpolation.Bezier)
            {
                continue;
            }

            (GraphPoint outgoing, GraphPoint incoming) = KeyframeGraph.Handles(keys[selected], keys[selected + 1], keys[0].Time);
            if (Near(pixel, HandlePixel(outgoing, keys[0].Time), radius))
            {
                return (selected, 0);
            }

            if (Near(pixel, HandlePixel(incoming, keys[0].Time), radius))
            {
                return (selected, 1);
            }
        }

        for (int i = 0; i < keys.Count; i++)
        {
            if (Near(pixel, Pixel(keys[i].Time, keys[i].Value), radius))
            {
                return (i, -1);
            }
        }

        return null;
    }

    public IReadOnlyList<Time> InRectangle(IReadOnlyList<Keyframe> keys, GraphPoint a, GraphPoint b)
    {
        ArgumentNullException.ThrowIfNull(keys);
        return keys.Where(key =>
        {
            GraphPoint p = Pixel(key.Time, key.Value);
            return p.Seconds >= Math.Min(a.Seconds, b.Seconds) && p.Seconds <= Math.Max(a.Seconds, b.Seconds)
                && p.Value >= Math.Min(a.Value, b.Value) && p.Value <= Math.Max(a.Value, b.Value);
        }).Select(key => key.Time).ToArray();
    }

    public static (double Low, double High) Fit(Parameter parameter, IReadOnlyList<GraphPoint> samples)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        ArgumentNullException.ThrowIfNull(samples);
        return KeyframeGraph.Range([.. samples, .. parameter.Keyframes.Select(k => new GraphPoint(0, k.Value))], 1e-6);
    }
}
