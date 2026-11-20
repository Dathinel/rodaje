using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Views.Timeline;

/// <summary>
/// Los instantes a los que se pega lo que se arrastra con el ajuste activo: bordes de los demás clips, el cursor,
/// marcadores, bordes de regiones y el cero. Se arma una vez al empezar el gesto.
/// </summary>
public sealed class SnapPoints
{
    /// <summary>A cuántos píxeles de un punto se pega.</summary>
    public const double ThresholdPixels = 8;

    private readonly long[] _points;

    private SnapPoints(long[] points) => _points = points;

    public static SnapPoints Empty { get; } = new([]);

    public int Count => _points.Length;

    /// <param name="exclude">Clips (o el marcador) que se arrastran: sus bordes no cuentan.</param>
    public static SnapPoints Collect(Sequence sequence, IReadOnlySet<string> exclude, Time? playhead)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(exclude);
        var points = new List<long> { 0 };
        foreach (Track track in sequence.AllTracks)
        {
            foreach (Clip clip in track.Clips)
            {
                if (!exclude.Contains(clip.Id))
                {
                    points.Add(clip.Start.Ticks);
                    points.Add(clip.End.Ticks);
                }
            }
        }

        if (playhead is { } p)
        {
            points.Add(p.Ticks);
        }

        points.AddRange(sequence.Markers.Where(m => !exclude.Contains(m.Id)).Select(m => m.Time.Ticks));
        foreach (Region region in sequence.Regions)
        {
            points.Add(region.Start.Ticks);
            points.Add(region.End.Ticks);
        }

        return new SnapPoints([.. points.Distinct().Order()]);
    }

    /// <summary>El punto más cercano a <paramref name="time"/> a menos de <paramref name="threshold"/>, si hay.</summary>
    public Time? Nearest(Time time, Time threshold)
    {
        if (_points.Length == 0)
        {
            return null;
        }

        int index = Array.BinarySearch(_points, time.Ticks);
        if (index >= 0)
        {
            return time;
        }

        index = ~index;
        long best = 0;
        long bestDistance = long.MaxValue;
        foreach (int i in (ReadOnlySpan<int>)[index - 1, index])
        {
            if (i >= 0 && i < _points.Length)
            {
                long distance = Math.Abs(_points[i] - time.Ticks);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = _points[i];
                }
            }
        }

        return bestDistance <= threshold.Ticks ? Time.FromTicks(best) : null;
    }

    /// <summary>
    /// Pega un tramo que se mueve entero: prueba su inicio y su final y se queda con el que necesita menos corrección.
    /// </summary>
    /// <returns>La corrección a sumar al tramo y el punto al que se pegó (para dibujar la guía).</returns>
    public (Time Correction, Time? Guide) SnapSpan(Time start, Time end, Time threshold)
    {
        Time? atStart = Nearest(start, threshold);
        Time? atEnd = Nearest(end, threshold);
        Time? startCorrection = atStart - start;
        Time? endCorrection = atEnd - end;
        if (startCorrection is { } s && (endCorrection is not { } e0 || Math.Abs(s.Ticks) <= Math.Abs(e0.Ticks)))
        {
            return (s, atStart);
        }

        return endCorrection is { } e ? (e, atEnd) : (Time.Zero, null);
    }
}
