using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Editing;

// No comprueban bloqueos ni validan: eso lo hace la operación pública que las llama.
internal static class TrackOps
{
    /// <summary>Vacía [<paramref name="from"/>, <paramref name="to"/>): quita, recorta o parte los clips que lo tocan.</summary>
    public static void ClearRange(EditContext ctx, Track track, Time from, Time to)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(track);
        if (from >= to)
        {
            return;
        }

        foreach (Clip clip in (List<Clip>)[.. track.Clips])
        {
            if (clip.End <= from || clip.Start >= to)
            {
                continue;
            }

            bool startsBefore = clip.Start < from;
            bool endsAfter = clip.End > to;

            if (startsBefore && endsAfter)
            {
                Clip right = SplitOff(ctx, clip, to);
                clip.TimeMap.TrimOut(from).ApplyTo(clip);
                AddClipSorted(track, right);
            }
            else if (startsBefore)
            {
                clip.TimeMap.TrimOut(from).ApplyTo(clip);
            }
            else if (endsAfter)
            {
                clip.TimeMap.TrimIn(to).ApplyTo(clip);
            }
            else
            {
                track.Clips.Remove(clip);
            }
        }
    }

    /// <summary>Abre un espacio en <paramref name="at"/>, partiendo el clip que pase por ahí.</summary>
    public static void InsertSpace(EditContext ctx, Track track, Time at, Time duration)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(track);
        ArgumentOutOfRangeException.ThrowIfLessThan(duration.Ticks, 0L, nameof(duration));
        if (duration == Time.Zero)
        {
            return;
        }

        Clip? straddling = track.Clips.Find(c => c.Start < at && at < c.End);
        if (straddling is not null)
        {
            AddClipSorted(track, SplitOff(ctx, straddling, at));
        }

        EditContext.ShiftClips(track, at, duration);
    }

    public static void AddClipSorted(Track track, Clip clip)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(clip);
        int index = track.Clips.FindIndex(c => c.Start > clip.Start);
        if (index < 0)
        {
            track.Clips.Add(clip);
        }
        else
        {
            track.Clips.Insert(index, clip);
        }
    }

    /// <summary>
    /// Recorta el clip a la izquierda de <paramref name="at"/> y devuelve la derecha sin agregarla a ninguna pista: un
    /// identificador nuevo, un enlace derivado, la misma velocidad y copias de los efectos.
    /// </summary>
    public static Clip SplitOff(EditContext ctx, Clip clip, Time at)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(clip);
        Clip right = clip.Clone();
        right.Id = ctx.Ids.NewId("clip");
        right.LinkId = ctx.DerivedLinkId(clip.LinkId);
        foreach (EffectInstance effect in right.Effects)
        {
            effect.Id = ctx.Ids.NewId("fx");
        }

        (ClipTimeMap leftMap, ClipTimeMap rightMap) = clip.TimeMap.SplitAt(at);
        rightMap.ApplyTo(right);
        leftMap.ApplyTo(clip);
        return right;
    }

    public static List<(Track Track, Clip Clip)> ResolveLinkGroup(EditContext ctx, string clipId)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (ctx.Sequence.FindClip(clipId) is null)
        {
            throw new EditException($"No existe el clip «{clipId}» en la secuencia.");
        }

        return ctx.Sequence.GetLinkGroup(clipId);
    }

    /// <summary>Une sin repetir los grupos enlazados de los clips pedidos.</summary>
    public static List<(Track Track, Clip Clip)> ExpandSelection(EditContext ctx, IEnumerable<string> clipIds)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(clipIds);
        var result = new List<(Track Track, Clip Clip)>();
        var seen = new HashSet<Clip>();
        foreach (string clipId in clipIds)
        {
            foreach ((Track track, Clip clip) in ResolveLinkGroup(ctx, clipId))
            {
                if (seen.Add(clip))
                {
                    result.Add((track, clip));
                }
            }
        }

        if (result.Count == 0)
        {
            throw new EditException("No hay ningún clip seleccionado.");
        }

        return result;
    }

    public static Clip? FindOverlap(Track track, Time from, Time to, IReadOnlySet<Clip>? ignore = null)
    {
        ArgumentNullException.ThrowIfNull(track);
        return track.Clips.Find(c => c.Start < to && c.End > from && (ignore is null || !ignore.Contains(c)));
    }

    public static void EnsureFree(Track track, Time from, Time to, IReadOnlySet<Clip>? ignore = null)
    {
        Clip? blocking = FindOverlap(track, from, to, ignore);
        if (blocking is not null)
        {
            throw new EditException($"El lugar está ocupado por el clip «{blocking.Id}» en la pista «{track.Name}».");
        }
    }

    /// <summary>Falla si una pista no bloqueada tiene un clip que se queda dentro del tramo a cerrar.</summary>
    public static void EnsureNothingStraddles(EditContext ctx, Time from, Time to, IReadOnlySet<Clip>? ignore = null)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        foreach (Track track in ctx.Sequence.AllTracks.Where(t => !t.Locked))
        {
            if (FindOverlap(track, from, to, ignore) is not null)
            {
                throw new EditException(
                    $"No se puede cerrar el hueco porque en la pista «{track.Name}» hay un clip que atraviesa ese tramo.");
            }
        }
    }

    /// <summary>Une tramos que se tocan o se solapan, ordenados.</summary>
    public static List<(Time Start, Time End)> MergeSpans(IEnumerable<(Time Start, Time End)> spans)
    {
        var merged = new List<(Time Start, Time End)>();
        foreach ((Time start, Time end) in spans.OrderBy(s => s.Start))
        {
            if (merged.Count > 0 && start <= merged[^1].End)
            {
                merged[^1] = (merged[^1].Start, Time.Max(merged[^1].End, end));
            }
            else
            {
                merged.Add((start, end));
            }
        }

        return merged;
    }

    public static string KindName(TrackKind kind) => kind == TrackKind.Video ? "video" : "audio";
}
