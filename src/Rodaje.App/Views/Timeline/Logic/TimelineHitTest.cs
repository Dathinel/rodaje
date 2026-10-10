using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Views.Timeline;

public enum HitKind
{
    /// <summary>Fuera de toda pista.</summary>
    None,

    /// <summary>En una pista, sin clip.</summary>
    Lane,

    ClipBody,

    /// <summary>El borde de entrada del clip (recortar el inicio).</summary>
    ClipStart,

    /// <summary>El borde de salida del clip (recortar el final).</summary>
    ClipEnd,
}

/// <summary>Lo que hay bajo el puntero en el área de pistas.</summary>
public readonly record struct TimelineHit(HitKind Kind, TrackRow? Row, Clip? Clip, Time Time);

public static class TimelineHitTest
{
    /// <summary>Ancho de la zona de agarre de cada borde; en clips angostos se achica para dejar cuerpo.</summary>
    public const double EdgeGrab = 7;

    /// <param name="x">X en el área visible.</param>
    /// <param name="contentY">Y en el contenido (ya sumado el desplazamiento vertical).</param>
    public static TimelineHit Hit(TrackLayout layout, TimelineViewport viewport, double x, double contentY)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(viewport);
        Time time = viewport.XToTime(x);
        if (layout.RowStrictlyAt(contentY) is not { } row)
        {
            return new TimelineHit(HitKind.None, null, null, time);
        }

        List<Clip> clips = row.Track.Clips;
        int index = FindClipIndex(clips, viewport, x);
        if (index < 0)
        {
            return new TimelineHit(HitKind.Lane, row, null, time);
        }

        Clip clip = clips[index];
        double left = viewport.TimeToX(clip.Start);
        double right = viewport.TimeToX(clip.End);
        double grab = Math.Min(EdgeGrab, (right - left) / 4);
        HitKind kind = x - left <= grab ? HitKind.ClipStart : right - x <= grab ? HitKind.ClipEnd : HitKind.ClipBody;
        return new TimelineHit(kind, row, clip, time);
    }

    /// <summary>El clip que cubre la x (búsqueda binaria: los clips de una pista están ordenados y no se solapan).</summary>
    public static int FindClipIndex(List<Clip> clips, TimelineViewport viewport, double x)
    {
        ArgumentNullException.ThrowIfNull(clips);
        ArgumentNullException.ThrowIfNull(viewport);
        int lo = 0;
        int hi = clips.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            Clip clip = clips[mid];
            double left = viewport.TimeToX(clip.Start);
            double right = viewport.TimeToX(clip.End);
            if (x < left)
            {
                hi = mid - 1;
            }
            else if (x >= right)
            {
                lo = mid + 1;
            }
            else
            {
                return mid;
            }
        }

        return -1;
    }

    /// <summary>El primer índice de clip que termina después de <paramref name="time"/>: desde ahí empiezan los visibles.</summary>
    public static int FirstEndingAfter(List<Clip> clips, Time time)
    {
        ArgumentNullException.ThrowIfNull(clips);
        int lo = 0;
        int hi = clips.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (clips[mid].End <= time)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }

    /// <summary>Los clips que tocan el rectángulo de selección (x en el área visible, y en el contenido).</summary>
    public static List<string> Marquee(TrackLayout layout, TimelineViewport viewport, double x1, double y1, double x2, double y2)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(viewport);
        (double left, double right) = x1 <= x2 ? (x1, x2) : (x2, x1);
        (double top, double bottom) = y1 <= y2 ? (y1, y2) : (y2, y1);
        Time from = viewport.XToTime(left);
        Time to = viewport.XToTime(right);
        var ids = new List<string>();
        foreach (TrackRow row in layout.Rows)
        {
            if (row.Bottom <= top || row.Top >= bottom)
            {
                continue;
            }

            foreach (Clip clip in row.Track.Clips)
            {
                if (clip.End > from && clip.Start < to)
                {
                    ids.Add(clip.Id);
                }
            }
        }

        return ids;
    }
}
