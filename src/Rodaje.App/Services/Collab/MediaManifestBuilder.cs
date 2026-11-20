using Rodaje.Core.Model;

namespace Rodaje.App.Services.Collab;

/// <summary>
/// El lado del anfitrión: saca el tamaño y el SHA-256 de cada medio del proyecto que existe en su disco, sin ruta. Calcular el
/// SHA-256 lee el archivo entero (del orden de 1 GB/s), así que se guarda por (ruta, tamaño, fecha) y va en segundo plano.
/// </summary>
public sealed class MediaManifestBuilder(Func<string, CancellationToken, Task<string?>>? hasher = null)
{
    private readonly Func<string, CancellationToken, Task<string?>> _hasher = hasher ?? MediaMatch.Sha256Async;
    private readonly Dictionary<(string Path, long Length, long Ticks), string> _cache = [];

    public async Task<IReadOnlyList<MediaManifestEntry>> BuildAsync(
        IEnumerable<MediaRef> media,
        Func<MediaRef, string> resolvePath,
        IProgress<(int Done, int Total)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        List<MediaRef> list = [.. media.Take(MediaManifest.MaxEntries)];
        List<MediaManifestEntry> result = [];
        int done = 0;
        foreach (MediaRef m in list)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report((done++, list.Count));
            FileInfo info = new(resolvePath(m));
            if (!info.Exists)
            {
                continue;
            }

            var key = (info.FullName, info.Length, info.LastWriteTimeUtc.Ticks);
            if (!_cache.TryGetValue(key, out string? hash))
            {
                hash = await _hasher(info.FullName, cancellationToken).ConfigureAwait(false);
                if (hash is null)
                {
                    continue;
                }

                _cache[key] = hash;
            }

            result.Add(new MediaManifestEntry(m.Id, string.IsNullOrWhiteSpace(m.Name) ? MediaManifest.DisplayName(m.Path) : MediaManifest.DisplayName(m.Name), info.Length, hash));
        }

        progress?.Report((list.Count, list.Count));
        return result;
    }

    /// <summary>
    /// La ruta del medio cuyo archivo entero tiene este SHA-256 (el mismo cálculo con caché que
    /// <see cref="BuildAsync"/>); <c>null</c> si ninguno coincide o su archivo no se puede leer. Es cómo una petición
    /// remota busca un medio sin que viaje ninguna ruta: el hash se comprueba en este equipo.
    /// </summary>
    public async Task<string?> FindByHashAsync(
        IEnumerable<MediaRef> media,
        Func<MediaRef, string> resolvePath,
        string hash,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(media);
        ArgumentNullException.ThrowIfNull(resolvePath);
        foreach (MediaRef m in media.Take(MediaManifest.MaxEntries))
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileInfo info;
            try
            {
                info = new FileInfo(resolvePath(m));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (!info.Exists)
            {
                continue;
            }

            var key = (info.FullName, info.Length, info.LastWriteTimeUtc.Ticks);
            if (!_cache.TryGetValue(key, out string? found))
            {
                found = await _hasher(info.FullName, cancellationToken).ConfigureAwait(false);
                if (found is null)
                {
                    continue;
                }

                _cache[key] = found;
            }

            if (found == hash)
            {
                return info.FullName;
            }
        }

        return null;
    }
}
