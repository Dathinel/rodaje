using FotoToma.Core.Model;

namespace FotoToma.Core.Editing;

public static partial class TimelineEdits
{
    /// <summary>Agrega una pista vacía en la posición dada de la lista de su tipo.</summary>
    /// <returns>Identificador de la pista nueva.</returns>
    public static string AddTrack(EditContext ctx, TrackKind kind, int index, string name)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        List<Track> list = TracksSupport.ListOf(ctx.Sequence, kind);
        if (index < 0 || index > list.Count)
        {
            throw new EditException($"La posición {index} no existe: la pista nueva debe ir entre la 0 y la {list.Count}.");
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new EditException("La pista necesita un nombre.");
        }

        var track = new Track { Id = ctx.Ids.NewId("track"), Kind = kind, Name = name };
        list.Insert(index, track);
        ctx.EnsureValid();
        return track.Id;
    }

    /// <summary>Borra la pista con sus clips; los enlaces que queden huérfanos se limpian fuera de pistas bloqueadas.</summary>
    public static void RemoveTrack(EditContext ctx, string trackId)
    {
        Track track = TrimmingSupport.ResolveTrack(ctx, trackId);
        EditContext.EnsureUnlocked(track);

        TracksSupport.ListOf(ctx.Sequence, track.Kind).Remove(track);
        TrimmingSupport.DropOrphanLinks(ctx.Sequence, track.Clips.Select(c => c.LinkId));
        ctx.EnsureValid();
    }

    /// <summary>Reordena la pista; una bloqueada no se mueve porque el orden decide qué capa tapa a cuál.</summary>
    public static void MoveTrack(EditContext ctx, string trackId, int newIndex)
    {
        Track track = TrimmingSupport.ResolveTrack(ctx, trackId);
        EditContext.EnsureUnlocked(track);
        List<Track> list = TracksSupport.ListOf(ctx.Sequence, track.Kind);
        if (newIndex < 0 || newIndex >= list.Count)
        {
            throw new EditException($"La posición {newIndex} no existe: la pista debe quedar entre la 0 y la {list.Count - 1}.");
        }

        list.Remove(track);
        list.Insert(newIndex, track);
        ctx.EnsureValid();
    }

    /// <summary>Cambia los campos no nulos; en una pista bloqueada solo se permite (des)bloquear, salvo que se desbloquee en la misma llamada.</summary>
    public static void SetTrackState(
        EditContext ctx,
        string trackId,
        bool? muted = null,
        bool? locked = null,
        bool? hidden = null,
        string? name = null)
    {
        Track track = TrimmingSupport.ResolveTrack(ctx, trackId);
        if (name is not null && string.IsNullOrWhiteSpace(name))
        {
            throw new EditException("La pista necesita un nombre.");
        }

        bool changesOther = (muted is not null && muted != track.Muted)
            || (hidden is not null && hidden != track.Hidden)
            || (name is not null && name != track.Name);
        if (changesOther && track.Locked && locked != false)
        {
            EditContext.EnsureUnlocked(track);
        }

        track.Muted = muted ?? track.Muted;
        track.Hidden = hidden ?? track.Hidden;
        track.Name = name ?? track.Name;
        track.Locked = locked ?? track.Locked;
        ctx.EnsureValid();
    }

    /// <summary>Activa o desactiva el clip y sus enlazados.</summary>
    public static void SetClipEnabled(EditContext ctx, string clipId, bool enabled)
    {
        List<(Track Track, Clip Clip)> group = TrimmingSupport.ResolveGroup(ctx, clipId);
        foreach ((Track track, _) in group)
        {
            EditContext.EnsureUnlocked(track);
        }

        foreach ((_, Clip clip) in group)
        {
            clip.Enabled = enabled;
        }

        ctx.EnsureValid();
    }

    /// <summary>Enlaza dos o más clips de pistas distintas; los enlaces anteriores que queden huérfanos se limpian.</summary>
    /// <returns>Identificador del enlace nuevo.</returns>
    public static string LinkClips(EditContext ctx, IReadOnlyCollection<string> clipIds)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(clipIds);
        List<string> ids = [.. clipIds.Distinct(StringComparer.Ordinal)];
        if (ids.Count < 2)
        {
            throw new EditException("Para enlazar hacen falta al menos dos clips distintos.");
        }

        var members = new List<(Track Track, Clip Clip)>(ids.Count);
        var usedTracks = new HashSet<Track>();
        foreach (string id in ids)
        {
            (Track Track, Clip Clip) member = ctx.Sequence.FindClip(id)
                ?? throw new EditException($"No existe el clip «{id}» en la secuencia.");
            if (!usedTracks.Add(member.Track))
            {
                throw new EditException($"No se pueden enlazar dos clips de la misma pista «{member.Track.Name}».");
            }

            EditContext.EnsureUnlocked(member.Track);
            members.Add(member);
        }

        List<string?> previousLinks = members.ConvertAll(m => m.Clip.LinkId);
        string linkId = ctx.Ids.NewId("link");
        foreach ((_, Clip clip) in members)
        {
            clip.LinkId = linkId;
        }

        TrimmingSupport.DropOrphanLinks(ctx.Sequence, previousLinks);
        ctx.EnsureValid();
        return linkId;
    }

    /// <summary>El clip y todos sus enlazados pasan a ir sueltos.</summary>
    public static void UnlinkClips(EditContext ctx, string clipId)
    {
        List<(Track Track, Clip Clip)> group = TrimmingSupport.ResolveGroup(ctx, clipId);
        if (group[0].Clip.LinkId is null)
        {
            throw new EditException($"El clip «{group[0].Clip.Id}» no está enlazado con ningún otro.");
        }

        foreach ((Track track, _) in group)
        {
            EditContext.EnsureUnlocked(track);
        }

        foreach ((_, Clip clip) in group)
        {
            clip.LinkId = null;
        }

        ctx.EnsureValid();
    }
}

internal static class TracksSupport
{
    public static List<Track> ListOf(Sequence sequence, TrackKind kind) => kind switch
    {
        TrackKind.Video => sequence.VideoTracks,
        TrackKind.Audio => sequence.AudioTracks,
        _ => throw new EditException($"Tipo de pista desconocido: {kind}."),
    };
}
