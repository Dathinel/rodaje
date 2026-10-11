using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Editing;

public static partial class TimelineEdits
{
    /// <summary>Agrega un marcador en <paramref name="at"/>.</summary>
    /// <returns>Identificador del marcador nuevo.</returns>
    public static string AddMarker(EditContext ctx, Time at, string name)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(name);
        EnsureNotNegative(at, "El marcador");
        var marker = new Marker { Id = ctx.Ids.NewId("marker"), Time = at, Name = name };
        ctx.Sequence.Markers.Add(marker);
        EditContext.SortMarkers(ctx.Sequence);
        ctx.EnsureValid();
        return marker.Id;
    }

    public static void RemoveMarker(EditContext ctx, string markerId)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        Marker marker = ResolveMarker(ctx, markerId);
        ctx.Sequence.Markers.Remove(marker);
        ctx.EnsureValid();
    }

    public static void MoveMarker(EditContext ctx, string markerId, Time newTime)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        Marker marker = ResolveMarker(ctx, markerId);
        EnsureNotNegative(newTime, "El marcador");
        marker.Time = newTime;
        EditContext.SortMarkers(ctx.Sequence);
        ctx.EnsureValid();
    }

    /// <summary>Agrega una región <c>[start, end)</c>.</summary>
    /// <returns>Identificador de la región nueva.</returns>
    public static string AddRegion(EditContext ctx, Time start, Time end, string name)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(name);
        EnsureNotNegative(start, "La región");
        if (end <= start)
        {
            throw new EditException("La región tiene que terminar después de empezar.");
        }

        var region = new Region { Id = ctx.Ids.NewId("region"), Start = start, End = end, Name = name };
        ctx.Sequence.Regions.Add(region);
        EditContext.SortMarkers(ctx.Sequence);
        ctx.EnsureValid();
        return region.Id;
    }

    public static void RemoveRegion(EditContext ctx, string regionId)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        Region region = ctx.Sequence.Regions.Find(r => r.Id == regionId)
            ?? throw new EditException($"No existe la región «{regionId}» en la secuencia.");
        ctx.Sequence.Regions.Remove(region);
        ctx.EnsureValid();
    }

    /// <summary>
    /// Lleva el clip a la posición <paramref name="newIndex"/> de su pista y la deja sin huecos desde donde empezaba su
    /// primer clip, como la pista principal magnética. Los enlazados de cada clip que se corre se corren lo mismo.
    /// </summary>
    public static void ReorderOnMainTrack(EditContext ctx, string clipId, int newIndex)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        (Track track, Clip clip) = ctx.Sequence.FindClip(clipId)
            ?? throw new EditException($"No existe el clip «{clipId}» en la secuencia.");
        EditContext.EnsureUnlocked(track);
        if (newIndex < 0 || newIndex >= track.Clips.Count)
        {
            throw new EditException($"La posición {newIndex} no existe: el clip debe quedar entre la 0 y la {track.Clips.Count - 1}.");
        }

        List<Clip> order = [.. track.Clips];
        order.Remove(clip);
        order.Insert(newIndex, clip);

        Time cursor = track.Clips[0].Start;
        var partners = new List<(Track Track, Clip Clip, Time Delta)>();
        var shifts = new List<(Clip Clip, Time Delta)>();
        var pieces = new List<TrackKeyframes.Piece>();
        foreach (Clip c in order)
        {
            Time delta = cursor - c.Start;
            cursor += c.Duration;
            pieces.Add(new(c.Start, c.End, delta));
            if (delta == Time.Zero)
            {
                continue;
            }

            shifts.Add((c, delta));
            if (c.LinkId is null)
            {
                continue;
            }

            foreach ((Track t, Clip partner) in ctx.Sequence.GetLinkGroup(c.Id).Skip(1))
            {
                EditContext.EnsureUnlocked(t);
                if (partner.Start + delta < Time.Zero)
                {
                    throw new EditException($"El clip enlazado «{partner.Id}» quedaría antes del inicio de la línea de tiempo.");
                }

                partners.Add((t, partner, delta));
            }
        }

        EnsurePartnersFit(partners);
        pieces.Add(new(track.End, TrackKeyframes.ToTheEnd, cursor - track.End));
        TrackKeyframes.Move(track, pieces);
        foreach (IGrouping<Track, (Track Track, Clip Clip, Time Delta)> moved in partners.GroupBy(p => p.Track))
        {
            TrackKeyframes.Move(moved.Key, moved.Select(p => new TrackKeyframes.Piece(p.Clip.Start, p.Clip.End, p.Delta)));
        }

        foreach ((Clip c, Time delta) in shifts)
        {
            c.Start += delta;
        }

        track.Clips = order;
        foreach ((Track t, Clip partner, _) in partners)
        {
            t.Clips.Remove(partner);
        }

        foreach ((Track t, Clip partner, Time delta) in partners)
        {
            partner.Start += delta;
            TrackOps.AddClipSorted(t, partner);
        }

        ctx.EnsureValid();
    }

    // Antes de tocar nada: los enlazados corridos no pueden caer encima de otro clip de su pista.
    private static void EnsurePartnersFit(List<(Track Track, Clip Clip, Time Delta)> partners)
    {
        foreach (IGrouping<Track, (Track Track, Clip Clip, Time Delta)> group in partners.GroupBy(p => p.Track))
        {
            Dictionary<Clip, Time> moved = group.ToDictionary(p => p.Clip, p => p.Delta);
            List<(Time Start, Time End, Clip Clip)> spans = [.. group.Key.Clips
                .Select(c => moved.TryGetValue(c, out Time d) ? (c.Start + d, c.End + d, c) : (c.Start, c.End, c))
                .OrderBy(s => s.Item1)];
            for (int i = 1; i < spans.Count; i++)
            {
                if (spans[i].Start < spans[i - 1].End)
                {
                    throw new EditException(
                        $"El clip enlazado «{(moved.ContainsKey(spans[i].Clip) ? spans[i].Clip.Id : spans[i - 1].Clip.Id)}» se solaparía con otro clip en la pista «{group.Key.Name}».");
                }
            }
        }
    }

    private static Marker ResolveMarker(EditContext ctx, string markerId) =>
        ctx.Sequence.Markers.Find(m => m.Id == markerId)
        ?? throw new EditException($"No existe el marcador «{markerId}» en la secuencia.");

    private static void EnsureNotNegative(Time at, string what)
    {
        if (at < Time.Zero)
        {
            throw new EditException($"{what} no puede quedar antes del inicio de la línea de tiempo.");
        }
    }
}
