using System.Collections.Concurrent;
using Rodaje.Core.Model;
using Rodaje.Media.Proxies;

namespace Rodaje.App.Services.Collab;

/// <summary>
/// El <see cref="IRoomProxyMaker"/> de verdad: busca el medio del proyecto por el SHA-256 entero de su archivo (nunca por
/// una ruta recibida) y genera su proxy de sala con <see cref="ProxySettings.Room"/> (H.264 bajo + AAC estéreo) en un
/// almacén aparte dentro de la carpeta de la sala. Ese almacén se indexa por la huella entera, así que la limpieza del
/// <see cref="ProxyStore"/> de la vista previa no lo ve; el archivo vive lo que viva la carpeta de la sala. Las peticiones
/// con la misma huella comparten un único trabajo del <see cref="ProxyGenerator"/>.
/// </summary>
public sealed partial class RoomProxyMaker : IRoomProxyMaker, IDisposable
{
    private readonly FfmpegExecutable _ffmpeg;
    private readonly Func<IReadOnlyList<MediaRef>> _media;
    private readonly Func<MediaRef, string> _resolvePath;
    private readonly MediaManifestBuilder _builder;
    private readonly ProxyGeneratorOptions? _options;
    private readonly ConcurrentDictionary<string, Lazy<(ProxyStore Store, ProxyGenerator Generator)>> _stores = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="media">Los medios del proyecto del anfitrión, leídos al momento (los que añada después también cuentan).</param>
    /// <param name="resolvePath">Cómo llegar al archivo de cada medio en este equipo; lo que no se pueda leer no tiene proxy.</param>
    public RoomProxyMaker(FfmpegExecutable ffmpeg, Func<IReadOnlyList<MediaRef>> media, Func<MediaRef, string> resolvePath,
        MediaManifestBuilder? builder = null, ProxyGeneratorOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(ffmpeg);
        ArgumentNullException.ThrowIfNull(media);
        ArgumentNullException.ThrowIfNull(resolvePath);
        _ffmpeg = ffmpeg;
        _media = media;
        _resolvePath = resolvePath;
        _builder = builder ?? new MediaManifestBuilder();
        _options = options;
    }

    public async Task<RoomProxy?> MakeAsync(string mediaHash, string folder, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaHash);
        if (!MediaManifest.IsHash(mediaHash))
        {
            throw new ArgumentException("La huella del medio no es un SHA-256 válido.", nameof(mediaHash));
        }

        string? source = await _builder.FindByHashAsync(_media(), _resolvePath, mediaHash, cancellationToken).ConfigureAwait(false);
        if (source is null)
        {
            return null;
        }

        (ProxyStore _, ProxyGenerator generator) = Store(folder);
        SourceFingerprint fingerprint = await Task.Run(() => SourceFingerprint.Compute(source), cancellationToken).ConfigureAwait(false);
        // La clave del almacén de la sala es la huella entera del archivo, no la muestra de SourceFingerprint.
        ProxyEntry entry = await generator.GenerateAsync(fingerprint with { Key = mediaHash }, ProxySettings.Room,
            progress is null ? null : new Fraction(progress), cancellationToken).ConfigureAwait(false);

        FileInfo info = new(entry.ProxyPath);
        if (!info.Exists)
        {
            throw new CollabException("No se pudo preparar la copia ligera.");
        }

        string sha = await MediaMatch.Sha256Async(entry.ProxyPath, cancellationToken).ConfigureAwait(false)
            ?? throw new CollabException("No se pudo comprobar la copia ligera. Comprueba que el disco funciona e inténtalo otra vez.");
        return new RoomProxy(entry.ProxyPath, sha, info.Length);
    }

    public void Dispose()
    {
        foreach (Lazy<(ProxyStore Store, ProxyGenerator Generator)> entry in _stores.Values)
        {
            if (entry.IsValueCreated)
            {
                entry.Value.Generator.Dispose();
            }
        }
    }

    /// <summary>El almacén y el generador de la carpeta de la sala, compartidos por todas las peticiones que lleguen a esa carpeta.</summary>
    private (ProxyStore Store, ProxyGenerator Generator) Store(string folder)
    {
        string full = Path.GetFullPath(folder);
        return _stores.GetOrAdd(full, path => new Lazy<(ProxyStore, ProxyGenerator)>(() =>
        {
            var store = new ProxyStore(path);
            return (store, new ProxyGenerator(store, _ffmpeg, _options));
        })).Value;
    }

    /// <summary>El avance del codificador como fracción simple para el aviso «preparando».</summary>
    private sealed class Fraction(IProgress<double> progress) : IProgress<ProxyProgress>
    {
        public void Report(ProxyProgress value) => progress.Report(Math.Clamp(value.Fraction, 0, 1));
    }
}
