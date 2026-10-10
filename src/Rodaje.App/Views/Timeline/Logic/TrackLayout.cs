using Rodaje.Core.Model;

namespace Rodaje.App.Views.Timeline;

/// <summary>Una fila de pista: su lugar vertical en el contenido (sin desplazamiento) y si es la principal.</summary>
public sealed record TrackRow(Track Track, int Row, double Top, double Height, bool IsMain)
{
    public string TrackId => Track.Id;

    public TrackKind Kind => Track.Kind;

    public double Bottom => Top + Height;
}

/// <summary>
/// Orden y altura de las filas: arriba las pistas de video de la más alta a la más baja (la principal, la de más abajo,
/// es más alta y queda pegada al audio, como en CapCut) y debajo las de audio.
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
            double height = main ? MainRowHeight : VideoRowHeight;
            rows.Add(new TrackRow(track, rows.Count, y, height, main));
            y += height + RowGap;
        }

        if (sequence.VideoTracks.Count > 0 && sequence.AudioTracks.Count > 0)
        {
            y += SectionGap;
        }

        foreach (Track track in sequence.AudioTracks)
        {
            rows.Add(new TrackRow(track, rows.Count, y, AudioRowHeight, false));
            y += AudioRowHeight + RowGap;
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
}
