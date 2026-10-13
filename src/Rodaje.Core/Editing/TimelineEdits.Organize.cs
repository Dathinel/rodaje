using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Editing;

public static partial class TimelineEdits
{
    /// <summary>Pone el color de etiqueta (<c>#RRGGBB</c>, o <c>null</c> para el de su tipo) a los clips y sus enlazados.</summary>
    public static void SetClipColor(EditContext ctx, IEnumerable<string> clipIds, string? color)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (color is not null && !MarkerStyle.IsColor(color))
        {
            throw new EditException($"El color «{color}» no es #RRGGBB.");
        }

        List<(Track Track, Clip Clip)> targets = TrackOps.ExpandSelection(ctx, clipIds);
        foreach ((Track track, _) in targets)
        {
            EditContext.EnsureUnlocked(track);
        }

        foreach ((_, Clip clip) in targets)
        {
            clip.Color = color is null ? null : color.ToUpperInvariant();
        }

        ctx.EnsureValid();
    }

    /// <summary>
    /// Agrupa los clips (con sus enlazados): desde ahora se seleccionan y se mueven juntos. Los grupos anteriores de esos
    /// clips se funden en el nuevo; un grupo de un solo clip no tiene sentido y se rechaza.
    /// </summary>
    /// <returns>Identificador del grupo nuevo.</returns>
    public static string GroupClips(EditContext ctx, IEnumerable<string> clipIds)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        List<(Track Track, Clip Clip)> members = GroupClosure(ctx, TrackOps.ExpandSelection(ctx, clipIds));
        if (members.Count < 2)
        {
            throw new EditException("Para agrupar hacen falta al menos dos clips.");
        }

        foreach ((Track track, _) in members)
        {
            EditContext.EnsureUnlocked(track);
        }

        string groupId = ctx.Ids.NewId("group");
        foreach ((_, Clip clip) in members)
        {
            clip.GroupId = groupId;
        }

        ctx.EnsureValid();
        return groupId;
    }

    /// <summary>Deshace los grupos de los clips: todos sus miembros vuelven a ir sueltos (los enlaces se conservan).</summary>
    public static void UngroupClips(EditContext ctx, IEnumerable<string> clipIds)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        List<(Track Track, Clip Clip)> members = GroupClosure(ctx, TrackOps.ExpandSelection(ctx, clipIds));
        if (!members.Exists(m => m.Clip.GroupId is not null))
        {
            throw new EditException("Ninguno de esos clips está en un grupo.");
        }

        foreach ((Track track, Clip clip) in members)
        {
            if (clip.GroupId is not null)
            {
                EditContext.EnsureUnlocked(track);
            }
        }

        foreach ((_, Clip clip) in members)
        {
            clip.GroupId = null;
        }

        ctx.EnsureValid();
    }

    /// <summary>
    /// Los clips pedidos más sus enlazados y los miembros de sus grupos, y los enlazados de estos, hasta no sumar más.
    /// </summary>
    public static IReadOnlyList<(Track Track, Clip Clip)> ExpandWithGroups(Sequence sequence, IEnumerable<string> clipIds)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(clipIds);
        var seed = new List<(Track Track, Clip Clip)>();
        var seen = new HashSet<Clip>();
        foreach (string id in clipIds)
        {
            if (sequence.FindClip(id) is not null)
            {
                foreach ((Track track, Clip clip) in sequence.GetLinkGroup(id))
                {
                    if (seen.Add(clip))
                    {
                        seed.Add((track, clip));
                    }
                }
            }
        }

        return GroupClosure(sequence, seed);
    }

    /// <summary>
    /// Mueve los clips (con sus enlazados y sus grupos) <paramref name="delta"/> en el tiempo, sin cambiar de pista ni
    /// pisar nada: es el empujón cuadro a cuadro del teclado y el arrastre de un grupo.
    /// </summary>
    public static void MoveClips(EditContext ctx, IEnumerable<string> clipIds, Time delta)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        List<(Track Track, Clip Clip)> moving = GroupClosure(ctx, TrackOps.ExpandSelection(ctx, clipIds));
        var set = new HashSet<Clip>(moving.Select(m => m.Clip));
        foreach ((Track track, Clip clip) in moving)
        {
            EditContext.EnsureUnlocked(track);
            if (clip.Start + delta < Time.Zero)
            {
                throw new EditException($"El clip «{clip.Id}» quedaría antes del inicio de la línea de tiempo.");
            }
        }

        foreach ((Track track, Clip clip) in moving)
        {
            TrackOps.EnsureFree(track, clip.Start + delta, clip.End + delta, set);
        }

        foreach ((_, Clip clip) in moving)
        {
            clip.Start += delta;
        }

        foreach (Track track in moving.Select(m => m.Track).Distinct())
        {
            track.SortClips();
        }

        ctx.EnsureValid();
    }

    /// <summary>Cambia el alto de la pista (<c>null</c>: el de siempre). Es solo vista: vale también en una pista bloqueada.</summary>
    public static void SetTrackHeight(EditContext ctx, string trackId, int? height)
    {
        Track track = TrimmingSupport.ResolveTrack(ctx, trackId);
        track.Height = height is { } h ? Math.Clamp(h, Track.MinHeight, Track.MaxHeight) : null;
        ctx.EnsureValid();
    }

    private static List<(Track Track, Clip Clip)> GroupClosure(EditContext ctx, List<(Track Track, Clip Clip)> seed) =>
        GroupClosure(ctx.Sequence, seed);

    private static List<(Track Track, Clip Clip)> GroupClosure(Sequence sequence, List<(Track Track, Clip Clip)> seed)
    {
        var result = new List<(Track Track, Clip Clip)>(seed);
        var seen = new HashSet<Clip>(seed.Select(s => s.Clip));
        var groups = new HashSet<string>(StringComparer.Ordinal);
        var links = new HashSet<string>(StringComparer.Ordinal);
        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach ((_, Clip clip) in result)
            {
                if (clip.GroupId is { } g)
                {
                    groups.Add(g);
                }

                if (clip.LinkId is { } l)
                {
                    links.Add(l);
                }
            }

            foreach (Track track in sequence.AllTracks)
            {
                foreach (Clip clip in track.Clips)
                {
                    bool member = (clip.GroupId is { } g && groups.Contains(g)) || (clip.LinkId is { } l && links.Contains(l));
                    if (member && seen.Add(clip))
                    {
                        result.Add((track, clip));
                        grew = true;
                    }
                }
            }
        }

        return result;
    }
}
