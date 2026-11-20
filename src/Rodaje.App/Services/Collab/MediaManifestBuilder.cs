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
            string? path;
            try
            {
                // Un medio que llegó de otra persona puede no tener ruta en este equipo: sin ella ni archivo, se omite y se sigue.
                path = resolvePath(m);
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
            {
                continue;
            }

            try
            {
                FileInfo info = new(path);
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
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
            {
                // Un medio que no se puede leer no impide compartir el proyecto: entra sin su archivo.
            }
        }

        progress?.Report((list.Count, list.Count));
        return result;
    }
}
