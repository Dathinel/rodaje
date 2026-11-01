using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Editing;

/// <summary>Un clip copiado y de dónde salió: el tipo de pista y su número en la lista (0 es la de más abajo).</summary>
public sealed record CopiedClip(Clip Clip, TrackKind Kind, int TrackIndex);

/// <summary>
/// Lo que se copió con «Copiar clips» (Ctrl+C): copias independientes del modelo, con sus efectos, su velocidad y sus
/// enlaces, para pegarlas en el cursor aunque el proyecto haya cambiado mientras tanto. <see cref="Origin"/> es el inicio
/// del primero: al pegar, ese instante cae en el cursor y los demás guardan su distancia.
/// </summary>
public sealed class ClipClipboard
{
    private ClipClipboard(IReadOnlyList<CopiedClip> items, Time origin)
    {
        Items = items;
        Origin = origin;
    }

    public IReadOnlyList<CopiedClip> Items { get; }

    public Time Origin { get; }

    /// <summary>Fin del último clip copiado, medido desde <see cref="Origin"/>.</summary>
    public Time Length => Items.Select(i => i.Clip.End).DefaultIfEmpty(Origin).Max() - Origin;

    /// <summary>
    /// Copia los clips pedidos con sus enlazados y sus grupos (con <paramref name="alone"/>, solo ellos). Un clip
    /// compuesto no se copia: dos clips no pueden compartir la misma secuencia interior. <c>null</c> si no hay nada que copiar.
    /// </summary>
    public static ClipClipboard? Capture(Sequence sequence, IEnumerable<string> clipIds, bool alone = false)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(clipIds);
        IReadOnlyList<(Track Track, Clip Clip)> found = alone
            ? [.. clipIds.Distinct(StringComparer.Ordinal).Select(sequence.FindClip).OfType<(Track, Clip)>()]
            : TimelineEdits.ExpandWithGroups(sequence, clipIds);
        if (found.Count == 0)
        {
            return null;
        }

        if (found.Any(f => f.Clip.IsCompound))
        {
            throw new EditException("Un clip compuesto no se copia ni se duplica: entra en él y copia lo de dentro.");
        }

        var items = new List<CopiedClip>(found.Count);
        foreach ((Track track, Clip clip) in found.OrderBy(f => f.Clip.Start.Ticks))
        {
            int index = track.Kind == TrackKind.Video ? sequence.VideoTracks.IndexOf(track) : sequence.AudioTracks.IndexOf(track);
            Clip copy = clip.Clone();
            if (alone)
            {
                // Sin su pareja ni su grupo, la copia va suelta.
                copy.LinkId = null;
                copy.GroupId = null;
            }

            items.Add(new CopiedClip(copy, track.Kind, index));
        }

        return new ClipClipboard(items, items[0].Clip.Start);
    }
}
