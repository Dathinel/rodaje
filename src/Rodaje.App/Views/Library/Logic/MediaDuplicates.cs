using System.Security.Cryptography;

namespace Rodaje.App.Views.Library;

/// <summary>Un medio a revisar: su id y la ruta absoluta de su archivo.</summary>
public readonly record struct MediaFile(string MediaId, string Path);

/// <summary>
/// Medios repetidos en la biblioteca: el mismo archivo importado dos veces (misma ruta) o el mismo contenido en dos
/// archivos (mismo tamaño y misma huella del principio, el medio y el final, 64 KB de cada uno). Sin WinUI; lee el disco,
/// así que va fuera del hilo de la interfaz.
/// </summary>
public static class MediaDuplicates
{
    private const int SampleBytes = 64 * 1024;

    /// <summary>
    /// Los grupos de dos o más medios repetidos, cada uno en el orden dado. Los archivos que faltan o no se pueden leer se
    /// saltan.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<string>> Find(IEnumerable<MediaFile> files, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        var groups = new List<IReadOnlyList<string>>();
        var bySize = new Dictionary<long, List<MediaFile>>();
        var byPath = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (MediaFile file in files)
        {
            string full;
            try
            {
                full = System.IO.Path.GetFullPath(file.Path);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (!byPath.TryGetValue(full, out List<string>? same))
            {
                byPath[full] = same = [];
                if (Length(full) is { } length)
                {
                    if (!bySize.TryGetValue(length, out List<MediaFile>? sized))
                    {
                        bySize[length] = sized = [];
                    }

                    sized.Add(file with { Path = full });
                }
            }

            same.Add(file.MediaId);
        }

        // El contenido se compara entre rutas distintas; los ids de una misma ruta van juntos.
        var merged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (List<MediaFile> sized in bySize.Values.Where(v => v.Count > 1))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (IGrouping<string, MediaFile> same in sized.GroupBy(f => Fingerprint(f.Path) ?? "", StringComparer.Ordinal))
            {
                if (same.Key.Length == 0 || same.Count() < 2)
                {
                    continue;
                }

                List<string> ids = [.. same.SelectMany(f => byPath[f.Path])];
                groups.Add(ids);
                foreach (MediaFile f in same)
                {
                    merged.Add(f.Path);
                }
            }
        }

        foreach ((string path, List<string> ids) in byPath)
        {
            if (ids.Count > 1 && !merged.Contains(path))
            {
                groups.Add(ids);
            }
        }

        return groups;
    }

    /// <summary>Los medios que no tienen su archivo donde dice el proyecto.</summary>
    public static IReadOnlyList<string> Offline(IEnumerable<MediaFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        return [.. files.Where(f => !Exists(f.Path)).Select(f => f.MediaId)];
    }

    private static bool Exists(string path)
    {
        try
        {
            return File.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static long? Length(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>SHA-256 de tres trozos del archivo (principio, medio y final), o <c>null</c> si no se puede leer.</summary>
    private static string? Fingerprint(string path)
    {
        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, SampleBytes);
            long length = stream.Length;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[SampleBytes];
            foreach (long offset in new[] { 0L, Math.Max(0, (length / 2) - (SampleBytes / 2)), Math.Max(0, length - SampleBytes) })
            {
                stream.Position = offset;
                int read = stream.ReadAtLeast(buffer, (int)Math.Min(SampleBytes, length - offset), throwOnEndOfStream: false);
                hash.AppendData(buffer, 0, read);
            }

            return Convert.ToHexString(hash.GetHashAndReset());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
