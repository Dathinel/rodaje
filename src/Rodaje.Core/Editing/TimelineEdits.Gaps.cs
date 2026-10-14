using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Editing;

/// <summary>Un hueco de una pista, <c>[Start, End)</c>: el espacio vacío antes de un clip.</summary>
public readonly record struct TimelineGap(string TrackId, Time Start, Time End)
{
    public Time Duration => End - Start;
}

public static partial class TimelineEdits
{
    /// <summary>
    /// Los huecos de las pistas pedidas (todas si es <c>null</c>), por pista y en orden: el tramo vacío antes de cada clip,
    /// también antes del primero. Después del último no hay hueco. Los más cortos que <paramref name="minimum"/> no cuentan.
    /// </summary>
    public static IReadOnlyList<TimelineGap> FindGaps(Sequence sequence, IEnumerable<string>? trackIds = null, Time minimum = default)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        HashSet<string>? only = trackIds is null ? null : new HashSet<string>(trackIds, StringComparer.Ordinal);
        var gaps = new List<TimelineGap>();
        foreach (Track track in sequence.AllTracks)
        {
            if (only is not null && !only.Contains(track.Id))
            {
                continue;
            }

            Time end = Time.Zero;
            foreach (Clip clip in track.Clips)
            {
                if (clip.Start > end && clip.Start - end > minimum)
                {
                    gaps.Add(new TimelineGap(track.Id, end, clip.Start));
                }

                end = Time.Max(end, clip.End);
            }
        }

        return gaps;
    }

    /// <summary>El hueco siguiente (o anterior) a <paramref name="from"/> en las pistas pedidas, para «Ir al hueco».</summary>
    public static TimelineGap? NextGap(Sequence sequence, Time from, bool forward, IEnumerable<string>? trackIds = null)
    {
        IReadOnlyList<TimelineGap> gaps = FindGaps(sequence, trackIds);
        return forward
            ? gaps.Where(g => g.Start > from).OrderBy(g => g.Start).Cast<TimelineGap?>().FirstOrDefault()
            : gaps.Where(g => g.Start < from).OrderByDescending(g => g.Start).Cast<TimelineGap?>().FirstOrDefault();
    }

    /// <summary>
    /// Cierra todos los huecos de las pistas (del último al primero, para que cada uno se mida en su lugar), con el
    /// alcance del ripple. Un hueco que no se puede cerrar, o que desincronizaría clips enlazados (una pareja que empieza
    /// antes del hueco y no se corre), se deja y se cuenta aparte.
    /// </summary>
    /// <returns>Cuántos huecos se cerraron y cuántos se dejaron.</returns>
    public static (int Closed, int Skipped) CloseAllGaps(EditContext ctx, IEnumerable<string> trackIds, RippleScope scope)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(trackIds);
        List<Track> tracks = [.. trackIds.Distinct(StringComparer.Ordinal).Select(id => TrimmingSupport.ResolveTrack(ctx, id))];
        int closed = 0;
        int skipped = 0;
        foreach (Track track in tracks)
        {
            if (track.Locked)
            {
                continue;
            }

            // Cada hueco se cierra de atrás hacia adelante: cerrar uno no mueve los anteriores.
            foreach (TimelineGap gap in FindGaps(ctx.Sequence, [track.Id]).Reverse())
            {
                List<Track> before = [.. ctx.Sequence.AllTracks.Select(t => t.Clone())];
                List<Marker> markers = ctx.Sequence.Markers.ConvertAll(m => m.Clone());
                List<Region> regions = ctx.Sequence.Regions.ConvertAll(r => r.Clone());
                // Par a par: un desfase nuevo del mismo tamaño que otro que ya había no puede pasar por viejo.
                LinkSync.SyncSnapshot syncBefore = LinkSync.Snapshot(ctx.Sequence);
                try
                {
                    CloseGap(ctx, track.Id, gap.Start, scope);
                    LinkSync.EnsureNoNewDesync(ctx.Sequence, syncBefore, "Cerrar este hueco");
                    closed++;
                }
                catch (EditException)
                {
                    int i = 0;
                    foreach (Track t in ctx.Sequence.AllTracks)
                    {
                        t.CopyStateFrom(before[i++]);
                    }

                    ctx.Sequence.Markers = markers;
                    ctx.Sequence.Regions = regions;
                    skipped++;
                }
            }
        }

        if (closed == 0 && skipped == 0)
        {
            throw new EditException("No hay huecos que cerrar.");
        }

        if (closed == 0)
        {
            throw new EditException("Ningún hueco se puede cerrar sin desincronizar otra pista: hay clips que atraviesan esos tramos.");
        }

        ctx.EnsureValid();
        return (closed, skipped);
    }
}
