using FotoToma.Core.Model;
using FotoToma.Core.Timing;

namespace FotoToma.Core.Editing;

/// <summary>Operaciones de edición que modifican <see cref="EditContext.Sequence"/> en el sitio.</summary>
public static partial class TimelineEdits
{
    /// <summary>Mueve un clip y su grupo enlazado; solo el clip pedido puede cambiar de pista.</summary>
    public static void Move(EditContext ctx, string clipId, Time newStart, string? targetTrackId, PlacementMode mode)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        List<(Track Track, Clip Clip)> group = TrackOps.ResolveLinkGroup(ctx, clipId);
        (Track primaryTrack, Clip primary) = group[0];

        if (newStart < Time.Zero)
        {
            throw new EditException("El clip no puede empezar antes del inicio de la línea de tiempo.");
        }

        Track primaryDestination = primaryTrack;
        if (targetTrackId is not null)
        {
            primaryDestination = ctx.Sequence.FindTrack(targetTrackId)
                ?? throw new EditException($"No existe la pista «{targetTrackId}» en la secuencia.");
            if (primaryDestination.Kind != primaryTrack.Kind)
            {
                throw new EditException(
                    $"El clip «{primary.Id}» es de {TrackOps.KindName(primaryTrack.Kind)} y no puede ir a la pista de {TrackOps.KindName(primaryDestination.Kind)} «{primaryDestination.Name}».");
            }

            if (primaryDestination != primaryTrack && group.Skip(1).Any(g => g.Track == primaryDestination))
            {
                throw new EditException(
                    $"La pista «{primaryDestination.Name}» ya tiene un clip enlazado con «{primary.Id}», y un grupo enlazado no puede tener dos clips en la misma pista.");
            }
        }

        Time delta = newStart - primary.Start;
        var moves = new List<(Track From, Track To, Clip Clip)>(group.Count);
        for (int i = 0; i < group.Count; i++)
        {
            (Track track, Clip clip) = group[i];
            Track destination = i == 0 ? primaryDestination : track;
            EditContext.EnsureUnlocked(track);
            EditContext.EnsureUnlocked(destination);
            if (clip.Start + delta < Time.Zero)
            {
                throw new EditException(
                    $"El clip enlazado «{clip.Id}» quedaría antes del inicio de la línea de tiempo.");
            }

            moves.Add((track, destination, clip));
        }

        var moving = new HashSet<Clip>(group.Select(g => g.Clip));
        if (mode == PlacementMode.Strict)
        {
            foreach ((_, Track to, Clip clip) in moves)
            {
                TrackOps.EnsureFree(to, clip.Start + delta, clip.End + delta, moving);
            }
        }

        foreach ((Track from, _, Clip clip) in moves)
        {
            from.Clips.Remove(clip);
        }

        foreach ((_, Track to, Clip clip) in moves)
        {
            clip.Start += delta;
            PlacementMakeRoom(ctx, to, clip.Start, clip.Duration, mode);
            TrackOps.AddClipSorted(to, clip);
        }

        ctx.EnsureValid();
    }

    /// <summary>Coloca un fragmento de un medio; con <paramref name="linkedTrackId"/> crea también el clip enlazado del otro tipo.</summary>
    /// <returns>Los identificadores de los clips creados; el de <paramref name="trackId"/> va primero.</returns>
    public static IReadOnlyList<string> InsertMedia(
        EditContext ctx,
        string mediaId,
        Time sourceIn,
        Time sourceOut,
        string trackId,
        Time at,
        PlacementMode mode,
        string? linkedTrackId = null)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        (MediaRef media, Track track, Track? linkedTrack) =
            PlacementResolveMediaTargets(ctx, mediaId, sourceIn, sourceOut, trackId, linkedTrackId);

        if (at < Time.Zero)
        {
            throw new EditException("El clip no puede empezar antes del inicio de la línea de tiempo.");
        }

        Time duration = sourceOut - sourceIn;
        if (mode == PlacementMode.Strict)
        {
            TrackOps.EnsureFree(track, at, at + duration);
            if (linkedTrack is not null)
            {
                TrackOps.EnsureFree(linkedTrack, at, at + duration);
            }
        }

        var created = new List<(Track Track, Clip Clip)>
        {
            (track, PlacementNewClip(ctx, media, sourceIn, sourceOut, at)),
        };

        if (linkedTrack is not null)
        {
            created.Add((linkedTrack, PlacementNewClip(ctx, media, sourceIn, sourceOut, at)));
            string linkId = ctx.Ids.NewId("link");
            foreach ((_, Clip clip) in created)
            {
                clip.LinkId = linkId;
            }
        }

        foreach ((Track target, Clip clip) in created)
        {
            PlacementMakeRoom(ctx, target, at, duration, mode);
            TrackOps.AddClipSorted(target, clip);
        }

        ctx.EnsureValid();
        return [.. created.Select(c => c.Clip.Id)];
    }

    /// <summary>Agrega un fragmento de un medio después del último clip de la(s) pista(s) de destino.</summary>
    /// <returns>Los identificadores de los clips creados; el de <paramref name="trackId"/> va primero.</returns>
    public static IReadOnlyList<string> AppendMedia(
        EditContext ctx,
        string mediaId,
        Time sourceIn,
        Time sourceOut,
        string trackId,
        string? linkedTrackId = null)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        (_, Track track, Track? linkedTrack) =
            PlacementResolveMediaTargets(ctx, mediaId, sourceIn, sourceOut, trackId, linkedTrackId);
        Time at = linkedTrack is null ? track.End : Time.Max(track.End, linkedTrack.End);
        return InsertMedia(ctx, mediaId, sourceIn, sourceOut, trackId, at, PlacementMode.Strict, linkedTrackId);
    }

    /// <summary>Quita los clips (con sus enlazados) y deja el hueco donde estaban.</summary>
    public static void Lift(EditContext ctx, IEnumerable<string> clipIds)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        List<(Track Track, Clip Clip)> targets = TrackOps.ExpandSelection(ctx, clipIds);
        foreach ((Track track, _) in targets)
        {
            EditContext.EnsureUnlocked(track);
        }

        foreach ((Track track, Clip clip) in targets)
        {
            track.Clips.Remove(clip);
        }

        ctx.EnsureValid();
    }

    /// <summary>Quita los clips (con sus enlazados) y cierra el hueco que dejan.</summary>
    /// <remarks>
    /// Con <see cref="RippleScope.AllTracks"/> los tramos se unen antes de correr, para no correr dos veces
    /// el mismo tramo (video y su audio), y falla si otro clip queda a caballo de un tramo.
    /// </remarks>
    public static void RippleDelete(EditContext ctx, IEnumerable<string> clipIds, RippleScope scope)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        List<(Track Track, Clip Clip)> targets = TrackOps.ExpandSelection(ctx, clipIds);
        foreach ((Track track, _) in targets)
        {
            EditContext.EnsureUnlocked(track);
        }

        if (scope == RippleScope.Track)
        {
            foreach ((Track track, Clip clip) in targets.OrderByDescending(t => t.Clip.Start))
            {
                track.Clips.Remove(clip);
                ctx.RippleShift([track], clip.End, -clip.Duration, scope);
            }
        }
        else
        {
            var removed = new HashSet<Clip>(targets.Select(t => t.Clip));
            List<(Time Start, Time End)> spans = TrackOps.MergeSpans(targets.Select(t => (t.Clip.Start, t.Clip.End)));
            foreach ((Time start, Time end) in spans)
            {
                TrackOps.EnsureNothingStraddles(ctx, start, end, removed);
            }

            foreach ((Track track, Clip clip) in targets)
            {
                track.Clips.Remove(clip);
            }

            List<Track> editedTracks = [.. targets.Select(t => t.Track).Distinct()];
            for (int i = spans.Count - 1; i >= 0; i--)
            {
                (Time start, Time end) = spans[i];
                ctx.RippleShift(editedTracks, end, start - end, scope);
            }
        }

        ctx.EnsureValid();
    }

    /// <summary>Cierra el hueco que contiene <paramref name="at"/>; después del último clip no hay hueco.</summary>
    public static void CloseGap(EditContext ctx, string trackId, Time at, RippleScope scope)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        Track track = ctx.Sequence.FindTrack(trackId)
            ?? throw new EditException($"No existe la pista «{trackId}» en la secuencia.");
        EditContext.EnsureUnlocked(track);

        Time gapStart = Time.Zero;
        Clip? next = null;
        foreach (Clip clip in track.Clips)
        {
            if (at >= gapStart && at < clip.Start)
            {
                next = clip;
                break;
            }

            gapStart = clip.End;
        }

        if (next is null)
        {
            throw new EditException($"No hay un hueco en la pista «{track.Name}» en {at}.");
        }

        if (scope == RippleScope.AllTracks)
        {
            TrackOps.EnsureNothingStraddles(ctx, gapStart, next.Start);
        }

        ctx.RippleShift([track], next.Start, gapStart - next.Start, scope);
        ctx.EnsureValid();
    }

    private static (MediaRef Media, Track Track, Track? LinkedTrack) PlacementResolveMediaTargets(
        EditContext ctx,
        string mediaId,
        Time sourceIn,
        Time sourceOut,
        string trackId,
        string? linkedTrackId)
    {
        MediaRef media = ctx.Project.FindMedia(mediaId)
            ?? throw new EditException($"No existe el medio «{mediaId}» en el proyecto.");
        Track track = ctx.Sequence.FindTrack(trackId)
            ?? throw new EditException($"No existe la pista «{trackId}» en la secuencia.");
        PlacementEnsureStream(media, track);

        if (sourceIn < Time.Zero)
        {
            throw new EditException("El punto de entrada no puede ser negativo.");
        }

        if (sourceOut <= sourceIn)
        {
            throw new EditException("El punto de salida tiene que ir después del punto de entrada.");
        }

        if (sourceOut > media.Duration)
        {
            throw new EditException(
                $"El punto de salida ({sourceOut}) pasa el final de «{media.Name}», que dura {media.Duration}.");
        }

        EditContext.EnsureUnlocked(track);

        Track? linkedTrack = null;
        if (linkedTrackId is not null)
        {
            linkedTrack = ctx.Sequence.FindTrack(linkedTrackId)
                ?? throw new EditException($"No existe la pista «{linkedTrackId}» en la secuencia.");
            if (linkedTrack.Kind == track.Kind)
            {
                throw new EditException(
                    $"La pista enlazada «{linkedTrack.Name}» tiene que ser de {TrackOps.KindName(track.Kind == TrackKind.Video ? TrackKind.Audio : TrackKind.Video)}, porque la principal es de {TrackOps.KindName(track.Kind)}.");
            }

            PlacementEnsureStream(media, linkedTrack);
            EditContext.EnsureUnlocked(linkedTrack);
        }

        return (media, track, linkedTrack);
    }

    private static void PlacementEnsureStream(MediaRef media, Track track)
    {
        if (track.Kind == TrackKind.Video && !media.HasVideo)
        {
            throw new EditException($"«{media.Name}» no tiene imagen, así que no puede ir en la pista de video «{track.Name}».");
        }

        if (track.Kind == TrackKind.Audio && !media.HasAudio)
        {
            throw new EditException($"«{media.Name}» no tiene sonido, así que no puede ir en la pista de audio «{track.Name}».");
        }
    }

    private static Clip PlacementNewClip(EditContext ctx, MediaRef media, Time sourceIn, Time sourceOut, Time at) => new()
    {
        Id = ctx.Ids.NewId("clip"),
        MediaId = media.Id,
        SourceIn = sourceIn,
        SourceOut = sourceOut,
        Start = at,
    };

    private static void PlacementMakeRoom(EditContext ctx, Track track, Time at, Time duration, PlacementMode mode)
    {
        switch (mode)
        {
            case PlacementMode.Overwrite:
                TrackOps.ClearRange(ctx, track, at, at + duration);
                break;
            case PlacementMode.Insert:
                TrackOps.InsertSpace(ctx, track, at, duration);
                break;
            default:
                break;
        }
    }
}
