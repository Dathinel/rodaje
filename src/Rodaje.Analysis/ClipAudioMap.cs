using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.Analysis;

/// <summary>
/// Lleva los resultados del análisis (segundos desde el inicio del tramo decodificado) a la línea de tiempo. Se decodifica
/// el material que usa el clip, <c>[SourceIn, SourceOut)</c> de su mapeo; con velocidad o reversa el instante de la línea
/// de tiempo sale de <see cref="ClipTimeMap.TimelineOf"/>.
/// </summary>
public static class ClipAudioMap
{
    /// <summary>Tramo del medio que hay que decodificar para analizar el clip.</summary>
    public static (Time SourceIn, Time SourceOut) SourceWindow(Clip clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ClipTimeMap map = clip.TimeMap;
        return (map.SourceIn, map.SourceOut);
    }

    public static Time ToTimeline(Clip clip, double seconds)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ClipTimeMap map = clip.TimeMap;
        Time source = map.SourceIn + Time.FromSeconds(Math.Max(0, seconds));
        return source >= map.SourceOut && !map.Reverse ? map.End : map.TimelineOf(source);
    }

    public static (Time Start, Time End) ToTimeline(Clip clip, TimeRange range)
    {
        Time a = ToTimeline(clip, range.Start);
        Time b = ToTimeline(clip, range.End);
        return a <= b ? (a, b) : (b, a);
    }

    /// <summary>Los tramos en la línea de tiempo, ordenados, sin vacíos.</summary>
    public static List<(Time Start, Time End)> ToTimeline(Clip clip, IEnumerable<TimeRange> ranges)
    {
        ArgumentNullException.ThrowIfNull(ranges);
        return [.. ranges.Select(r => ToTimeline(clip, r)).Where(r => r.End > r.Start).OrderBy(r => r.Start)];
    }
}
