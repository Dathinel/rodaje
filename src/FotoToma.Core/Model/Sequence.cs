using FotoToma.Core.Timing;

namespace FotoToma.Core.Model;

/// <summary>
/// Una línea de tiempo con sus pistas. Las pistas de video se componen de abajo hacia arriba:
/// el índice 0 de <see cref="VideoTracks"/> es la capa de más abajo.
/// </summary>
public sealed class Sequence
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public Rational FrameRate { get; set; } = Rational.Fps30;

    public int Width { get; set; } = 1920;

    public int Height { get; set; } = 1080;

    public int SampleRate { get; set; } = 48000;

    public List<Track> VideoTracks { get; set; } = [];

    public List<Track> AudioTracks { get; set; } = [];

    public IEnumerable<Track> AllTracks => VideoTracks.Concat(AudioTracks);

    /// <summary>Final del último clip de cualquier pista.</summary>
    public Time Duration => AllTracks.SelectMany(t => t.Clips).Select(c => c.End).DefaultIfEmpty(Time.Zero).Max();

    public Track? FindTrack(string trackId) => AllTracks.FirstOrDefault(t => t.Id == trackId);

    public Track GetTrack(string trackId) =>
        FindTrack(trackId) ?? throw new KeyNotFoundException($"No existe la pista «{trackId}» en la secuencia.");

    /// <summary>Busca un clip y la pista que lo contiene.</summary>
    public (Track Track, Clip Clip)? FindClip(string clipId)
    {
        foreach (Track track in AllTracks)
        {
            Clip? clip = track.Clips.Find(c => c.Id == clipId);
            if (clip is not null)
            {
                return (track, clip);
            }
        }

        return null;
    }

    public (Track Track, Clip Clip) GetClip(string clipId) =>
        FindClip(clipId) ?? throw new KeyNotFoundException($"No existe el clip «{clipId}» en la secuencia.");

    /// <summary>
    /// Devuelve el clip y los que están enlazados con él (por ejemplo el video y su audio), que se
    /// editan juntos. El clip pedido va primero.
    /// </summary>
    public List<(Track Track, Clip Clip)> GetLinkGroup(string clipId)
    {
        (Track track, Clip clip) = GetClip(clipId);
        var group = new List<(Track, Clip)> { (track, clip) };
        if (clip.LinkId is null)
        {
            return group;
        }

        foreach (Track t in AllTracks)
        {
            foreach (Clip c in t.Clips)
            {
                if (c.LinkId == clip.LinkId && c.Id != clip.Id)
                {
                    group.Add((t, c));
                }
            }
        }

        return group;
    }
}
