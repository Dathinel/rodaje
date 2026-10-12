namespace Rodaje.App.Views.Library;

/// <summary>Los códecs leídos de un archivo; <see cref="Failed"/> si no se pudo leer.</summary>
public readonly record struct MediaCodecs(string? Video, string? Audio, bool Failed)
{
    public static MediaCodecs Unreadable { get; } = new(null, null, Failed: true);
}

/// <summary>Fecha de modificación y tamaño de un archivo, para saber si cambió.</summary>
public readonly record struct MediaFileStamp(long LastWriteUtcTicks, long Length);

/// <summary>
/// Códecs ya leídos, por ruta y por la fecha de modificación y el tamaño del archivo, sin WinUI. Un archivo que cambia
/// (se terminó de copiar, se reemplazó) se vuelve a leer, también si antes había fallado; uno igual no se vuelve a abrir.
/// Se usa desde el hilo de fondo de la lectura, así que es seguro entre hilos.
/// </summary>
public sealed class MediaCodecCache
{
    private readonly Dictionary<string, (MediaFileStamp Stamp, MediaCodecs Codecs)> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();

    /// <summary>La marca del archivo, o <c>null</c> si no existe o no se puede mirar.</summary>
    public static MediaFileStamp? StampOf(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? new MediaFileStamp(info.LastWriteTimeUtc.Ticks, info.Length) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Lo ya leído del archivo si no cambió desde entonces.</summary>
    public bool TryGet(string path, MediaFileStamp stamp, out MediaCodecs codecs)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(path, out (MediaFileStamp Stamp, MediaCodecs Codecs) entry) && entry.Stamp == stamp)
            {
                codecs = entry.Codecs;
                return true;
            }
        }

        codecs = default;
        return false;
    }

    public void Set(string path, MediaFileStamp stamp, MediaCodecs codecs)
    {
        lock (_lock)
        {
            _entries[path] = (stamp, codecs);
        }
    }

    /// <summary>
    /// Los códecs del archivo: de la caché si no cambió, o de <paramref name="probe"/>. Cualquier error de
    /// <paramref name="probe"/> cuenta como «no se pudo leer» solo para ese archivo; nunca sale de aquí, así que un
    /// medio raro no deja a los demás esperando. Sin marca (el archivo no está) no se guarda nada: se reintenta.
    /// </summary>
    public MediaCodecs Read(string path, Func<string, (string? Video, string? Audio)> probe)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(probe);
        MediaFileStamp? stamp = StampOf(path);
        if (stamp is { } known && TryGet(path, known, out MediaCodecs cached))
        {
            return cached;
        }

        MediaCodecs codecs;
        try
        {
            (string? video, string? audio) = probe(path);
            codecs = new MediaCodecs(video, audio, Failed: false);
        }
#pragma warning disable CA1031 // Un fallo inesperado al leer un medio no debe cortar la lectura de los demás.
        catch (Exception)
#pragma warning restore CA1031
        {
            codecs = MediaCodecs.Unreadable;
        }

        // La marca se toma de nuevo: si el archivo cambió mientras se leía, lo leído no se guarda con la marca vieja.
        if (stamp is { } before && StampOf(path) == before)
        {
            Set(path, before, codecs);
        }

        return codecs;
    }
}
