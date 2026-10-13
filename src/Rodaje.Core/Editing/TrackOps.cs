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
                Clip right = SplitOff(ctx, clip, to, tidy: false);
                clip.TimeMap.TrimOut(from).ApplyTo(clip);
                if (clip.Generator is { } generator)
                {
                    // Lo que se borra también era del título: los dos trozos siguen su animación entera.
                    generator.Edges.After += to - from;
                    Tidy(generator.Edges, generator.Style, clip.Duration);
                    Tidy(right.Generator!.Edges, generator.Style, right.Duration);
                }

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

    /// <summary>
    /// Abre un espacio en <paramref name="at"/> en las pistas pedidas sin desincronizar nada: se parte lo que pasa por
    /// ahí (con sus enlazados que también pasen), y se corre todo lo que sigue más los enlazados de lo que se corre, en la
    /// pista que estén, y lo que venga después de ellos en esas pistas. Una pista bloqueada que haría falta correr hace
    /// fallar la edición.
    /// </summary>
    public static void InsertSpaceLinked(EditContext ctx, IReadOnlyCollection<Track> tracks, Time at, Time duration)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(tracks);
        ArgumentOutOfRangeException.ThrowIfLessThan(duration.Ticks, 0L, nameof(duration));
        if (duration == Time.Zero)
        {
            return;
        }

        foreach (Track track in tracks)
        {
            EditContext.EnsureUnlocked(track);
            if (track.Clips.Find(c => c.Start < at && at < c.End) is { } straddling)
            {
                foreach ((Track partnerTrack, Clip partner) in ctx.Sequence.GetLinkGroup(straddling.Id))
                {
                    if (partner.Start < at && at < partner.End)
                    {
                        EditContext.EnsureUnlocked(partnerTrack);
                        AddClipSorted(partnerTrack, SplitOff(ctx, partner, at));
                    }
                    else if (partner.Start >= at && !ReferenceEquals(partner, straddling))
                    {
                        // Lo que queda entero después del corte va con la mitad derecha, que se corre con él.
                        partner.LinkId = ctx.DerivedLinkId(partner.LinkId);
                    }
                }
            }
        }

        // Desde dónde se corre cada pista: lo de las pistas pedidas desde «at», y lo enlazado con algo que se corre
        // desde ese clip, hasta que no se sume nada.
        var from = new Dictionary<Track, Time>();
        foreach (Track track in tracks)
        {
            from[track] = at;
        }

        Dictionary<Clip, Track> owner = ctx.Sequence.AllTracks.SelectMany(t => t.Clips.Select(c => (t, c))).ToDictionary(p => p.c, p => p.t);
        Dictionary<string, List<Clip>> links = owner.Keys.Where(c => c.LinkId is not null).GroupBy(c => c.LinkId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach ((Track track, Time start) in from.ToList())
            {
                foreach (Clip clip in track.Clips)
                {
                    if (clip.Start < start || clip.LinkId is null)
                    {
                        continue;
                    }

                    foreach (Clip partner in links[clip.LinkId])
                    {
                        Track partnerTrack = owner[partner];
                        if (!from.TryGetValue(partnerTrack, out Time current) || partner.Start < current)
                        {
                            from[partnerTrack] = partner.Start;
                            grew = true;
                        }
                    }
                }
            }
        }

        foreach ((Track track, Time start) in from)
        {
            if (!tracks.Contains(track) && track.Clips.Exists(c => c.Start >= start))
            {
                EditContext.EnsureUnlocked(track);
            }
        }

        foreach ((Track track, Time start) in from)
        {
            EditContext.ShiftClips(track, start, duration);
        }
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
    /// identificador nuevo, un enlace derivado, la misma velocidad y copias de los efectos. En un generador el corte es un
    /// borde interno: cada trozo guarda cuánto del título queda al otro lado y sigue su animación (D-029).
    /// </summary>
    public static Clip SplitOff(EditContext ctx, Clip clip, Time at, bool tidy = true)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(clip);
        Clip right = clip.Clone();
        right.Id = ctx.Ids.NewId("clip");
        right.LinkId = ctx.DerivedLinkId(clip.LinkId);
        MaskEdits.RenewIds(ctx, right.Effects, "fx");

        (ClipTimeMap leftMap, ClipTimeMap rightMap) = clip.TimeMap.SplitAt(at);
        rightMap.ApplyTo(right);
        leftMap.ApplyTo(clip);
        if (clip.Generator is { } generator)
        {
            GeneratorEdges leftEdges = generator.Edges;
            GeneratorEdges rightEdges = right.Generator!.Edges;
            leftEdges.After += right.Duration;
            rightEdges.Before += clip.Duration;
            if (tidy)
            {
                Tidy(leftEdges, generator.Style, clip.Duration);
                Tidy(rightEdges, generator.Style, right.Duration);
            }
        }

        return right;
    }

    /// <summary>
    /// Tras dividir, cada trozo sigue la animación del título entero (D-029, sin parpadeo en el corte). Un borde cuya
    /// animación quedó entera fuera del trozo se apaga, y el desfase que ya no cambia nada se olvida: un corte lejos de la
    /// entrada y la salida deja los bordes internos apagados y sin desfase, como siempre.
    /// </summary>
    private static void Tidy(GeneratorEdges edges, TextStyle? style, Time duration)
    {
        long entry = edges.In ? TitleMotion.Ticks(style?.In) : 0;
        long exit = edges.Out ? TitleMotion.Ticks(style?.Out) : 0;
        long whole = edges.Before.Ticks + duration.Ticks + edges.After.Ticks;
        if (TitleMotion.Fit(entry, exit, whole).Stretch < 1)
        {
            // Encogidas, la forma depende del largo entero: se conserva tal cual.
            return;
        }

        if (edges.In && edges.Before > Time.Zero && edges.Before.Ticks >= entry)
        {
            edges.In = false;
        }

        if (edges.Out && edges.After > Time.Zero && edges.After.Ticks >= exit)
        {
            edges.Out = false;
        }

        entry = edges.In ? entry : 0;
        exit = edges.Out ? exit : 0;
        if (!edges.In && exit <= duration.Ticks + edges.After.Ticks)
        {
            edges.Before = Time.Zero;
        }

        if (!edges.Out && entry <= edges.Before.Ticks + duration.Ticks)
        {
            edges.After = Time.Zero;
        }
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
