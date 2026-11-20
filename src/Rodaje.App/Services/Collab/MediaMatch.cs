using System.Security.Cryptography;
using System.Text.Json;
using Rodaje.Core.Editing;

namespace Rodaje.App.Services.Collab;

/// <summary>Un medio del proyecto de la sala, sin ruta: lo que el anfitrión cuenta de él (tamaño y SHA-256 del archivo entero).</summary>
public sealed record MediaManifestEntry(string MediaId, string Name, long Size, string Hash);

/// <summary>El manifiesto de medios que viaja del anfitrión al invitado. Todo lo que trae se trata como ajeno.</summary>
public static class MediaManifest
{
    public const string Type = "medios";
    public const int MaxEntries = 5000;

    public static JsonElement ToJson(IEnumerable<MediaManifestEntry> entries)
    {
        using MemoryStream ms = new();
        using (Utf8JsonWriter w = new(ms))
        {
            w.WriteStartObject();
            w.WriteString("tipo", Type);
            w.WriteStartArray("medios");
            foreach (MediaManifestEntry e in entries.Take(MaxEntries))
            {
                w.WriteStartObject();
                w.WriteString("id", e.MediaId);
                w.WriteString("nombre", e.Name);
                w.WriteNumber("tam", e.Size);
                w.WriteString("sha256", e.Hash);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        using JsonDocument doc = JsonDocument.Parse(ms.ToArray());
        return doc.RootElement.Clone();
    }

    /// <summary>Lee un manifiesto; lo que no cumple (huella mal escrita, tamaño negativo, nombre con ruta) se descarta, no se arregla.</summary>
    public static IReadOnlyList<MediaManifestEntry> Parse(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object
            || !message.TryGetProperty("tipo", out JsonElement type) || type.ValueKind != JsonValueKind.String || type.GetString() != Type
            || !message.TryGetProperty("medios", out JsonElement list) || list.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        List<MediaManifestEntry> result = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (JsonElement item in list.EnumerateArray().Take(MaxEntries))
        {
            if (item.ValueKind != JsonValueKind.Object
                || Text(item, "id") is not { Length: > 0 and <= 64 } id
                || Text(item, "nombre") is not { Length: > 0 and <= 260 } name
                || Text(item, "sha256") is not { } hash || !IsHash(hash)
                || !item.TryGetProperty("tam", out JsonElement size) || !size.TryGetInt64(out long bytes) || bytes < 0
                || !seen.Add(id))
            {
                continue;
            }

            result.Add(new MediaManifestEntry(id, DisplayName(name), bytes, hash));
        }

        return result;
    }

    public static bool IsHash(string s) => s.Length == 64 && s.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>El nombre sin carpetas ni caracteres de control: solo se muestra, nunca se usa como ruta.</summary>
    public static string DisplayName(string name)
    {
        string clean = new([.. name.Where(c => !char.IsControl(c))]);
        int cut = clean.LastIndexOfAny(['\\', '/', ':']);
        string last = cut >= 0 ? clean[(cut + 1)..] : clean;
        return last.Length == 0 ? "medio" : last;
    }

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
}

/// <summary>Lo que dio buscar los medios de la sala en las carpetas del invitado.</summary>
/// <param name="Found">Medio → archivo con el mismo contenido (SHA-256 entero).</param>
/// <param name="Missing">Los que no se encontraron: quedan sin conexión.</param>
/// <param name="CloudOnly">Archivos con el tamaño buscado que solo están en la nube y no se abrieron (abrirlos los descarga).</param>
/// <param name="Truncated">Una carpeta tenía más archivos de los que se miran.</param>
public sealed record MediaMatchReport(
    IReadOnlyDictionary<string, string> Found,
    IReadOnlyList<MediaManifestEntry> Missing,
    int FilesScanned,
    int CloudOnly,
    bool Truncated);

/// <summary>
/// Busca en las carpetas que elige el invitado los medios del proyecto de la sala. Compara por contenido: primero el tamaño
/// (barato) y solo de lo que coincide el SHA-256 entero; nunca por nombre ni por ruta, que cambian de un equipo a otro.
/// </summary>
public sealed class MediaMatch(IRelinkFileSystem? files = null, Func<string, CancellationToken, Task<string?>>? hasher = null)
{
    public const int MaxFiles = 500_000;

    private readonly IRelinkFileSystem _files = files ?? new DiskRelinkFileSystem(_ => null);
    private readonly Func<string, CancellationToken, Task<string?>> _hasher = hasher ?? Sha256Async;

    public async Task<MediaMatchReport> SearchAsync(
        IReadOnlyList<MediaManifestEntry> wanted,
        IEnumerable<string> folders,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Dictionary<long, List<MediaManifestEntry>> bySize = [];
        foreach (MediaManifestEntry e in wanted)
        {
            if (!bySize.TryGetValue(e.Size, out List<MediaManifestEntry>? list))
            {
                bySize[e.Size] = list = [];
            }

            list.Add(e);
        }

        Dictionary<string, string> found = new(StringComparer.Ordinal);
        HashSet<string> visited = new(StringComparer.OrdinalIgnoreCase);
        int scanned = 0, cloud = 0;
        bool truncated = false;
        foreach (string folder in folders.Where(Directory.Exists))
        {
            foreach (RelinkFile file in _files.EnumerateFiles(folder, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++scanned > MaxFiles)
                {
                    truncated = true;
                    break;
                }

                if (scanned % 200 == 0)
                {
                    progress?.Report(scanned);
                }

                if (!bySize.TryGetValue(file.Length, out List<MediaManifestEntry>? candidates)
                    || candidates.All(c => found.ContainsKey(c.MediaId)) || !visited.Add(file.Path))
                {
                    continue;
                }

                if (file.CloudOnly)
                {
                    cloud++;
                    continue;
                }

                string? hash = await _hasher(file.Path, cancellationToken).ConfigureAwait(false);
                if (hash is null)
                {
                    continue;
                }

                foreach (MediaManifestEntry c in candidates.Where(c => c.Hash == hash && !found.ContainsKey(c.MediaId)))
                {
                    found[c.MediaId] = file.Path;
                }
            }

            if (truncated || wanted.All(w => found.ContainsKey(w.MediaId)))
            {
                break;
            }
        }

        progress?.Report(scanned);
        return new MediaMatchReport(found, [.. wanted.Where(w => !found.ContainsKey(w.MediaId))], scanned, cloud, truncated);
    }

    /// <summary>SHA-256 del archivo entero en minúsculas; <c>null</c> si no se puede leer (en uso, sin permiso, desapareció).</summary>
    public static async Task<string?> Sha256Async(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using FileStream stream = new(path, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.ReadWrite | FileShare.Delete,
                BufferSize = 1 << 20,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            });
            return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
