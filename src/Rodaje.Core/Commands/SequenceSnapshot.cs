using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Commands;

/// <summary>Copia profunda del estado de una secuencia, base del deshacer.</summary>
/// <remarks>
/// Al restaurar, las pistas conservan su identidad de objeto, pero los clips son copias nuevas: fuera del
/// modelo, los clips se referencian por <see cref="Clip.Id"/>.
/// </remarks>
public sealed class SequenceSnapshot
{
    private readonly string _id;
    private readonly string _name;
    private readonly Rational _frameRate;
    private readonly int _width;
    private readonly int _height;
    private readonly int _sampleRate;
    private readonly TrackEntry[] _videoTracks;
    private readonly TrackEntry[] _audioTracks;
    private readonly Marker[] _markers;
    private readonly Region[] _regions;

    private SequenceSnapshot(Sequence sequence)
    {
        _id = sequence.Id;
        _name = sequence.Name;
        _frameRate = sequence.FrameRate;
        _width = sequence.Width;
        _height = sequence.Height;
        _sampleRate = sequence.SampleRate;
        _videoTracks = [.. sequence.VideoTracks.Select(t => new TrackEntry(t, t.Clone()))];
        _audioTracks = [.. sequence.AudioTracks.Select(t => new TrackEntry(t, t.Clone()))];
        _markers = [.. sequence.Markers.Select(m => m.Clone())];
        _regions = [.. sequence.Regions.Select(r => r.Clone())];
    }

    public string SequenceId => _id;

    internal IReadOnlyList<TrackEntry> VideoTracks => _videoTracks;

    internal IReadOnlyList<TrackEntry> AudioTracks => _audioTracks;

    public static SequenceSnapshot Capture(Sequence sequence)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        return new SequenceSnapshot(sequence);
    }

    /// <summary>Restaura el estado capturado; se puede llamar cuantas veces haga falta.</summary>
    public void Restore(Sequence sequence)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        sequence.Id = _id;
        sequence.Name = _name;
        sequence.FrameRate = _frameRate;
        sequence.Width = _width;
        sequence.Height = _height;
        sequence.SampleRate = _sampleRate;
        RestoreTracks(sequence.VideoTracks, _videoTracks);
        RestoreTracks(sequence.AudioTracks, _audioTracks);
        sequence.Markers.Clear();
        sequence.Markers.AddRange(_markers.Select(m => m.Clone()));
        sequence.Regions.Clear();
        sequence.Regions.AddRange(_regions.Select(r => r.Clone()));
    }

    /// <summary>Igualdad profunda con el estado capturado, incluida la identidad de las pistas.</summary>
    public bool Matches(Sequence sequence)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        return sequence.Id == _id
            && sequence.Name == _name
            && sequence.FrameRate == _frameRate
            && sequence.Width == _width
            && sequence.Height == _height
            && sequence.SampleRate == _sampleRate
            && TracksMatch(sequence.VideoTracks, _videoTracks)
            && TracksMatch(sequence.AudioTracks, _audioTracks)
            && sequence.Markers.Count == _markers.Length
            && sequence.Markers.Zip(_markers).All(p => MarkerEquals(p.First, p.Second))
            && sequence.Regions.Count == _regions.Length
            && sequence.Regions.Zip(_regions).All(p => RegionEquals(p.First, p.Second));
    }

    public static bool MarkerEquals(Marker a, Marker b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return a.Id == b.Id && a.Time == b.Time && a.Name == b.Name;
    }

    public static bool RegionEquals(Region a, Region b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return a.Id == b.Id && a.Start == b.Start && a.End == b.End && a.Name == b.Name;
    }

    /// <summary>Compara contenido y marcas de bloqueo, silencio y visibilidad.</summary>
    public static bool TrackStateEquals(Track a, Track b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return a.Locked == b.Locked
            && a.Muted == b.Muted
            && a.Hidden == b.Hidden
            && TrackContentEquals(a, b);
    }

    /// <summary>Compara identificador, tipo, nombre y clips, sin las marcas.</summary>
    public static bool TrackContentEquals(Track a, Track b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return a.Id == b.Id
            && a.Kind == b.Kind
            && a.Name == b.Name
            && SequenceEqual(a.Clips, b.Clips, ClipStateEquals);
    }

    public static bool ClipStateEquals(Clip a, Clip b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return a.Id == b.Id
            && a.MediaId == b.MediaId
            && a.SourceIn == b.SourceIn
            && a.SourceOut == b.SourceOut
            && a.Start == b.Start
            && a.LinkId == b.LinkId
            && a.Enabled == b.Enabled
            && SequenceEqual(a.Effects, b.Effects, EffectStateEquals);
    }

    public static bool EffectStateEquals(EffectInstance a, EffectInstance b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (a.Id != b.Id || a.Type != b.Type || a.Enabled != b.Enabled || a.Parameters.Count != b.Parameters.Count)
        {
            return false;
        }

        foreach ((string key, Parameter parameter) in a.Parameters)
        {
            if (!b.Parameters.TryGetValue(key, out Parameter? other) || !ParameterStateEquals(parameter, other))
            {
                return false;
            }
        }

        return true;
    }

    public static bool ParameterStateEquals(Parameter a, Parameter b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return a.Value.Equals(b.Value) && a.Keyframes.SequenceEqual(b.Keyframes);
    }

    private static bool SequenceEqual<T>(List<T> a, List<T> b, Func<T, T, bool> equals)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        for (int i = 0; i < a.Count; i++)
        {
            if (!equals(a[i], b[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static void RestoreTracks(List<Track> target, TrackEntry[] entries)
    {
        target.Clear();
        foreach (TrackEntry entry in entries)
        {
            entry.Track.CopyStateFrom(entry.State);
            target.Add(entry.Track);
        }
    }

    private static bool TracksMatch(List<Track> current, TrackEntry[] entries)
    {
        if (current.Count != entries.Length)
        {
            return false;
        }

        for (int i = 0; i < entries.Length; i++)
        {
            if (!ReferenceEquals(current[i], entries[i].Track) || !TrackStateEquals(current[i], entries[i].State))
            {
                return false;
            }
        }

        return true;
    }

    internal readonly record struct TrackEntry(Track Track, Track State);
}
