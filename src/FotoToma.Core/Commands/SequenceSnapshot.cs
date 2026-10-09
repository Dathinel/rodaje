using FotoToma.Core.Model;
using FotoToma.Core.Timing;

namespace FotoToma.Core.Commands;

/// <summary>
/// Foto completa del estado de una secuencia: sus propiedades, qué pistas tiene y en qué orden, y una
/// copia profunda de cada pista con sus clips. Es la base del deshacer de las ediciones: en vez de
/// escribir a mano la operación inversa de cada edición, se guarda la foto de antes y la de después.
/// </summary>
/// <remarks>
/// <para>
/// Al restaurar, las pistas vuelven a ser los mismos <em>objetos</em> <see cref="Track"/> que había al
/// tomar la foto, en las mismas listas y el mismo orden, así que una referencia a una pista que guarde
/// la interfaz sigue siendo válida después de deshacer o rehacer.
/// </para>
/// <para>
/// Los clips, en cambio, se restauran como copias nuevas: un objeto <see cref="Clip"/> guardado fuera
/// del modelo deja de pertenecer a la secuencia tras deshacer o rehacer. La interfaz debe referirse a
/// los clips por su identificador (<see cref="Clip.Id"/>), que sí se conserva, y buscarlos de nuevo con
/// <see cref="Sequence.FindClip"/>.
/// </para>
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
    }

    /// <summary>Identificador de la secuencia fotografiada.</summary>
    public string SequenceId => _id;

    /// <summary>Pistas de video fotografiadas: el objeto original y la copia de su estado.</summary>
    internal IReadOnlyList<TrackEntry> VideoTracks => _videoTracks;

    /// <summary>Pistas de audio fotografiadas: el objeto original y la copia de su estado.</summary>
    internal IReadOnlyList<TrackEntry> AudioTracks => _audioTracks;

    /// <summary>Toma la foto del estado actual de la secuencia.</summary>
    public static SequenceSnapshot Capture(Sequence sequence)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        return new SequenceSnapshot(sequence);
    }

    /// <summary>
    /// Deja la secuencia exactamente como estaba al tomar la foto. Las listas de pistas se vacían y se
    /// vuelven a llenar (no se reemplazan), y cada pista recupera su estado con clips recién copiados,
    /// así que la foto puede restaurarse cuantas veces haga falta.
    /// </summary>
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
    }

    /// <summary>
    /// Indica si la secuencia está igual que en la foto: mismas propiedades, mismos objetos de pista en
    /// el mismo orden y mismo estado profundo de cada pista.
    /// </summary>
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
            && TracksMatch(sequence.AudioTracks, _audioTracks);
    }

    /// <summary>Compara todo el estado de dos pistas, incluidas las marcas de bloqueo, silencio y visibilidad.</summary>
    public static bool TrackStateEquals(Track a, Track b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return a.Locked == b.Locked
            && a.Muted == b.Muted
            && a.Hidden == b.Hidden
            && TrackContentEquals(a, b);
    }

    /// <summary>
    /// Compara el contenido editable de dos pistas: identificador, tipo, nombre y clips (con efectos,
    /// parámetros y keyframes). No mira las marcas de bloqueo, silencio ni visibilidad.
    /// </summary>
    public static bool TrackContentEquals(Track a, Track b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return a.Id == b.Id
            && a.Kind == b.Kind
            && a.Name == b.Name
            && SequenceEqual(a.Clips, b.Clips, ClipStateEquals);
    }

    /// <summary>Compara todos los campos de dos clips, incluidos sus efectos.</summary>
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

    /// <summary>Compara dos efectos con todos sus parámetros.</summary>
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

    /// <summary>Compara el valor y los keyframes de dos parámetros.</summary>
    public static bool ParameterStateEquals(Parameter a, Parameter b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        // Keyframe es un record: su igualdad ya compara tiempo, valor e interpolación.
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

    /// <summary>Una pista fotografiada: el objeto vivo y la copia de su estado en ese momento.</summary>
    internal readonly record struct TrackEntry(Track Track, Track State);
}
