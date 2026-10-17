using Rodaje.Media;
using Rodaje.Media.Probe;

namespace Rodaje.App.Services;

/// <summary>Lo que se supo de un archivo al importarlo: su información si se puede usar, o el motivo si no.</summary>
public sealed record MediaProbeResult(string Path, MediaInfo? Info, string? Problem)
{
    public bool IsUsable => Info is not null && Problem is null;
}

/// <summary>Lee los archivos que se importan. Se llama fuera del hilo de la interfaz, varios a la vez.</summary>
public interface IMediaProber
{
    MediaProbeResult Probe(string path);
}

/// <summary>
/// Empieza a leer archivos antes de que se pidan (por ejemplo, los de la línea de comandos mientras se crea la ventana)
/// y entrega ese resultado cuando llegan; lo demás pasa al lector de siempre.
/// </summary>
public sealed class PrefetchingProber(IMediaProber inner) : IMediaProber
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<Task<MediaProbeResult>>> _pending =
        new(StringComparer.OrdinalIgnoreCase);

    public void Prefetch(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        foreach (string path in paths)
        {
            string full = System.IO.Path.GetFullPath(path);
            var pending = new Lazy<Task<MediaProbeResult>>(() => Task.Run(() => inner.Probe(full)));
            if (_pending.TryAdd(full, pending))
            {
                // Probe puede consumir la entrada antes de llegar aquí: Lazy inicia la tarea una sola vez.
                _ = pending.Value;
            }
        }
    }

    public MediaProbeResult Probe(string path) =>
        _pending.TryRemove(path, out Lazy<Task<MediaProbeResult>>? early) ? early.Value.GetAwaiter().GetResult() : inner.Probe(path);
}

/// <summary>Lectura real con FFmpeg.</summary>
public sealed class FfmpegMediaProber : IMediaProber
{
    public MediaProbeResult Probe(string path)
    {
        if (!FfmpegLibrary.TryInitialize(out string? failure))
        {
            return new MediaProbeResult(path, null, failure);
        }

        string? problem = MediaSupport.Classify(path, out MediaInfo? info);
        return new MediaProbeResult(path, info, problem);
    }
}
