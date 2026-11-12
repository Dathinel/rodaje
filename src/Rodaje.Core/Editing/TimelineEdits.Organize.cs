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
    /// pisar nada: es el empujón cuadro a cuadro del teclado.
    /// </summary>
    public static void MoveClips(EditContext ctx, IEnumerable<string> clipIds, Time delta) =>
        MoveClips(ctx, clipIds, delta, TrackKind.Video, 0);

    /// <summary>
    /// Mueve los clips juntos: todos <paramref name="delta"/> en el tiempo y los de las pistas de <paramref name="kind"/>,
    /// además, <paramref name="trackOffset"/> pistas de ese tipo (los demás quedan en la suya, como los enlazados de un
    /// clip que cambia de pista). Nada se pisa: si algo no cabe, no se mueve ninguno. Es el arrastre de una selección; con
    /// <paramref name="alone"/>, solo los clips pedidos, sin enlazados ni grupos (ignorar grupos).
    /// </summary>
    public static void MoveClips(EditContext ctx, IEnumerable<string> clipIds, Time delta, TrackKind kind, int trackOffset, bool alone = false)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        List<(Track Track, Clip Clip)> moving = Moving(ctx, clipIds, alone);
        var set = new HashSet<Clip>(moving.Select(m => m.Clip));
        var moves = new List<(Track From, Track To, Clip Clip)>(moving.Count);
        foreach ((Track track, Clip clip) in moving)
        {
            EditContext.EnsureUnlocked(track);
            if (clip.Start + delta < Time.Zero)
            {
                throw new EditException($"El clip «{clip.Id}» quedaría antes del inicio de la línea de tiempo.");
            }

            Track to = track.Kind == kind && trackOffset != 0 ? ShiftedTrack(ctx.Sequence, track, trackOffset) : track;
            EditContext.EnsureUnlocked(to);
            moves.Add((track, to, clip));
        }

        foreach ((_, Track to, Clip clip) in moves)
        {
            TrackOps.EnsureFree(to, clip.Start + delta, clip.End + delta, set);
        }

        foreach ((Track from, Track to, Clip clip) in moves)
        {
            clip.Start += delta;
            if (from != to)
            {
                from.Clips.Remove(clip);
                TrackOps.AddClipSorted(to, clip);
            }
        }

        foreach (Track track in moves.Select(m => m.From).Distinct())
        {
            track.SortClips();
        }

        ctx.EnsureValid();
    }

    /// <summary>
    /// Pista principal magnética: los clips de la selección que están en ella van juntos, en su orden, a la posición
    /// <paramref name="index"/> entre los demás, y la pista queda sin huecos (los enlazados se corren con su clip). El
    /// resto de la selección se corre lo mismo que el primero del bloque.
    /// </summary>
    public static void MoveClipsOnMainTrack(EditContext ctx, IEnumerable<string> clipIds, string mainTrackId, int index, bool alone = false)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        List<(Track Track, Clip Clip)> moving = Moving(ctx, clipIds, alone);
        Track main = TrimmingSupport.ResolveTrack(ctx, mainTrackId);
        var set = new HashSet<Clip>(moving.Select(m => m.Clip));
        List<Clip> block = main.Clips.FindAll(set.Contains);
        if (block.Count == 0)
        {
            throw new EditException("Ninguno de esos clips está en la pista principal.");
        }

        List<Clip> rest = main.Clips.FindAll(c => !set.Contains(c));
        if (index < 0 || index > rest.Count)
        {
            throw new EditException($"La posición {index} no existe: el bloque debe quedar entre la 0 y la {rest.Count}.");
        }

        List<Clip> order = [.. rest.Take(index), .. block, .. rest.Skip(index)];
        Clip anchor = block[0];
        Time before = anchor.Start;
        for (int i = 0; i < order.Count; i++)
        {
            if (main.Clips[i] != order[i])
            {
                ReorderOnMainTrack(ctx, order[i].Id, i);
            }
        }

        var mainLinks = new HashSet<string>(main.Clips.Select(c => c.LinkId).OfType<string>(), StringComparer.Ordinal);
        string[] others = [.. moving
            .Where(m => m.Track != main && (m.Clip.LinkId is not { } link || !mainLinks.Contains(link)))
            .Select(m => m.Clip.Id)];
        Time delta = anchor.Start - before;
        if (others.Length > 0 && delta != Time.Zero)
        {
            MoveClips(ctx, others, delta, TrackKind.Video, 0, alone: true);
        }

        ctx.EnsureValid();
    }

    private static List<(Track Track, Clip Clip)> Moving(EditContext ctx, IEnumerable<string> clipIds, bool alone)
    {
        ArgumentNullException.ThrowIfNull(clipIds);
        if (!alone)
        {
            return GroupClosure(ctx, TrackOps.ExpandSelection(ctx, clipIds));
        }

        List<(Track Track, Clip Clip)> moving = [.. clipIds.Distinct(StringComparer.Ordinal).Select(id => ctx.Sequence.FindClip(id)
            ?? throw new EditException($"No existe el clip «{id}» en la secuencia."))];
        return moving.Count > 0 ? moving : throw new EditException("No hay ningún clip seleccionado.");
    }

    private static Track ShiftedTrack(Sequence sequence, Track track, int offset)
    {
        List<Track> tracks = track.Kind == TrackKind.Video ? sequence.VideoTracks : sequence.AudioTracks;
        int index = tracks.IndexOf(track) + offset;
        return index >= 0 && index < tracks.Count
            ? tracks[index]
            : throw new EditException($"No hay otra pista de {TrackOps.KindName(track.Kind)} hacia ese lado para «{track.Name}».");
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
