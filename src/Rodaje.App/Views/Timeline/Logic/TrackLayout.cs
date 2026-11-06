using Rodaje.Core.Model;

namespace Rodaje.App.Views.Timeline;

/// <summary>Una fila de pista: su lugar vertical en el contenido (sin desplazamiento) y si es la principal.</summary>
public sealed record TrackRow(Track Track, int Row, double Top, double Height, bool IsMain)
{
    public string TrackId => Track.Id;

    public TrackKind Kind => Track.Kind;

    public double Bottom => Top + Height;

    /// <summary>Si la fila asoma en la franja vertical [<paramref name="top"/>, <paramref name="bottom"/>) del contenido.</summary>
    public bool Overlaps(double top, double bottom) => Bottom > top && Top < bottom;
}

/// <summary>
/// Orden y altura de las filas: arriba las pistas de video de la más alta a la más baja (la principal, la de más abajo,
/// es más alta y queda pegada al audio) y debajo las de audio. Una pista con su propio alto (<see cref="Track.Height"/>) lo usa.
/// </summary>
public sealed class TrackLayout
{
    public const double VideoRowHeight = 52;
    public const double MainRowHeight = 68;
    public const double AudioRowHeight = 42;
    public const double RowGap = 2;
    public const double SectionGap = 8;
    public const double TopPadding = 6;

    private readonly List<TrackRow> _rows;

    private TrackLayout(List<TrackRow> rows, double height)
    {
        _rows = rows;
        TotalHeight = height;
    }

    public IReadOnlyList<TrackRow> Rows => _rows;

    public double TotalHeight { get; }

    public static TrackLayout Build(Sequence sequence, Track? mainTrack)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        var rows = new List<TrackRow>(sequence.VideoTracks.Count + sequence.AudioTracks.Count);
        double y = TopPadding;
        for (int i = sequence.VideoTracks.Count - 1; i >= 0; i--)
        {
            Track track = sequence.VideoTracks[i];
            bool main = ReferenceEquals(track, mainTrack);
            double height = track.Height ?? (main ? MainRowHeight : VideoRowHeight);
            rows.Add(new TrackRow(track, rows.Count, y, height, main));
            y += height + RowGap;
        }

        if (sequence.VideoTracks.Count > 0 && sequence.AudioTracks.Count > 0)
        {
            y += SectionGap;
        }

        foreach (Track track in sequence.AudioTracks)
        {
            double height = track.Height ?? AudioRowHeight;
            rows.Add(new TrackRow(track, rows.Count, y, height, false));
            y += height + RowGap;
        }

        return new TrackLayout(rows, y + TopPadding);
    }

    /// <summary>La fila bajo una y del contenido; entre dos filas, la más cercana.</summary>
    public TrackRow? RowAt(double y)
    {
        if (_rows.Count == 0)
        {
            return null;
        }

        TrackRow? best = null;
        double bestDistance = double.MaxValue;
        foreach (TrackRow row in _rows)
        {
            if (y >= row.Top && y < row.Bottom)
            {
                return row;
            }

            double distance = y < row.Top ? row.Top - y : y - row.Bottom;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = row;
            }
        }

        return best;
    }

    /// <summary>La fila bajo la y, solo si cae dentro de ella.</summary>
    public TrackRow? RowStrictlyAt(double y) => _rows.Find(r => y >= r.Top && y < r.Bottom);

    public TrackRow? Find(string trackId) => _rows.Find(r => r.TrackId == trackId);

    /// <summary>La posición de la pista en la lista de su tipo (en video, 0 es la principal, la de más abajo).</summary>
    public int IndexInKind(string trackId)
    {
        if (Find(trackId) is not { } row)
        {
            return -1;
        }

        List<TrackRow> kind = _rows.FindAll(r => r.Kind == row.Kind);
        int shown = kind.IndexOf(row);
        return row.Kind == TrackKind.Video ? kind.Count - 1 - shown : shown;
    }

    /// <summary>
    /// Adónde iría la pista arrastrada con el puntero en <paramref name="y"/>: su posición nueva en la lista de su tipo y
    /// la y de la línea que lo indica. Solo entre las de su tipo y nunca por debajo de la principal.
    /// </summary>
    public (int Index, double LineY)? ReorderSlot(string trackId, double y)
    {
        if (Find(trackId) is not { } row)
        {
            return null;
        }

        List<TrackRow> others = _rows.FindAll(r => r.Kind == row.Kind && r != row);
        if (others.Count == 0)
        {
            return null;
        }

        int slot = others.Count(r => (r.Top + r.Bottom) / 2 < y);
        if (others[^1].IsMain)
        {
            slot = Math.Min(slot, others.Count - 1);
        }

        double lineY = slot == 0 ? others[0].Top - (RowGap / 2) : others[slot - 1].Bottom + (RowGap / 2);
        int index = row.Kind == TrackKind.Video ? others.Count - slot : slot;
        return (index, lineY);
    }
}
