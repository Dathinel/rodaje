using FotoToma.Core.Model;
using FotoToma.Core.Timing;

namespace FotoToma.Core.Editing;

/// <summary>
/// Piezas básicas que comparten las operaciones de colocación: vaciar un tramo de una pista, abrir
/// espacio en ella, partir un clip y ubicar un clip en su lugar según la posición.
/// </summary>
/// <remarks>
/// Ninguna de estas funciones valida bloqueos ni deja la secuencia comprobada; eso lo hace la operación
/// pública que las usa, que termina con <see cref="EditContext.EnsureValid"/>.
/// </remarks>
internal static class TrackOps
{
    /// <summary>
    /// Deja vacío el tramo [<paramref name="from"/>, <paramref name="to"/>) de la pista: quita los clips que
    /// caen dentro, recorta los que entran o salen del tramo y parte en dos el que lo cubre entero.
    /// </summary>
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
                // El clip cubre todo el tramo: la parte de la derecha pasa a ser un clip nuevo.
                Clip right = SplitOff(ctx, clip, to);
                clip.SourceOut = clip.ToSourceTime(from);
                AddClipSorted(track, right);
            }
            else if (startsBefore)
            {
                clip.SourceOut = clip.ToSourceTime(from);
            }
            else if (endsAfter)
            {
                clip.SourceIn = clip.ToSourceTime(to);
                clip.Start = to;
            }
            else
            {
                track.Clips.Remove(clip);
            }
        }
    }

    /// <summary>
    /// Abre un espacio de <paramref name="duration"/> en <paramref name="at"/>: si un clip pasa por ese
    /// instante se parte ahí, y todo lo que empieza en <paramref name="at"/> o después se corre a la derecha.
    /// </summary>
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

    /// <summary>Agrega el clip a la pista en la posición que le corresponde según su inicio.</summary>
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
    /// Parte el clip en <paramref name="at"/>: el original se queda con la parte izquierda y se devuelve la
    /// derecha como clip nuevo (sin agregarlo a ninguna pista), con identificador propio, enlace derivado y
    /// efectos copiados con identificadores nuevos.
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

        right.SourceIn = clip.ToSourceTime(at);
        right.Start = at;
        clip.SourceOut = right.SourceIn;
        return right;
    }

    /// <summary>Busca el clip y su grupo enlazado; si no existe lanza un error para el usuario.</summary>
    public static List<(Track Track, Clip Clip)> ResolveLinkGroup(EditContext ctx, string clipId)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (ctx.Sequence.FindClip(clipId) is null)
        {
            throw new EditException($"No existe el clip «{clipId}» en la secuencia.");
        }

        return ctx.Sequence.GetLinkGroup(clipId);
    }

    /// <summary>
    /// Une los grupos enlazados de todos los clips pedidos, sin repetir ninguno. Lanza un error si la
    /// selección está vacía o nombra un clip que no existe.
    /// </summary>
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

    /// <summary>Busca el primer clip de la pista, fuera de <paramref name="ignore"/>, que toca el tramo [from, to).</summary>
    public static Clip? FindOverlap(Track track, Time from, Time to, IReadOnlySet<Clip>? ignore = null)
    {
        ArgumentNullException.ThrowIfNull(track);
        return track.Clips.Find(c => c.Start < to && c.End > from && (ignore is null || !ignore.Contains(c)));
    }

    /// <summary>Lanza un error que nombra al clip que ya ocupa el tramo, si lo hay.</summary>
    public static void EnsureFree(Track track, Time from, Time to, IReadOnlySet<Clip>? ignore = null)
    {
        Clip? blocking = FindOverlap(track, from, to, ignore);
        if (blocking is not null)
        {
            throw new EditException($"El lugar está ocupado por el clip «{blocking.Id}» en la pista «{track.Name}».");
        }
    }

    /// <summary>
    /// Antes de cerrar el tramo [from, to) en todas las pistas, comprueba que ninguna pista no bloqueada
    /// tenga ahí un clip que se quede (fuera de <paramref name="ignore"/>); si lo tuviera, al correr lo de
    /// la derecha quedaría encima de él.
    /// </summary>
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

    /// <summary>Une tramos que se tocan o se solapan y los devuelve ordenados de izquierda a derecha.</summary>
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

    /// <summary>Nombre del tipo de pista para los mensajes.</summary>
    public static string KindName(TrackKind kind) => kind == TrackKind.Video ? "video" : "audio";
}
