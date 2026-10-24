using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Rodaje.Packages.Download;

/// <summary>Qué trae una entrada del índice.</summary>
public enum IndexEntryKind
{
    Content,
    Module,
    Model,

    /// <summary>Una herramienta aparte (zip autocontenido) que instala el Lanzador; Rodaje la ignora.</summary>
    App,
}

/// <summary>Un archivo publicado: qué es, de dónde se baja, su SHA-256 y su tamaño exactos, y desde qué versión del programa sirve.</summary>
public sealed record IndexEntry(string Id, PackageVersion Version, IndexEntryKind Kind, Uri Url, string Sha256, long Size, PackageVersion? MinApp)
{
    public bool WorksWith(PackageVersion appVersion) => MinApp is null || appVersion.CompareTo(MinApp) >= 0;
}

/// <summary>
/// El índice de lo que se puede descargar (<c>indice.json</c>), ya con la firma comprobada:
/// <c>{ "formatVersion": 1, "generated": "…", "entries": [ { "id", "version", "kind", "url", "sha256", "size", "minApp" } ] }</c>.
/// La firma se comprueba antes de leer una sola propiedad; una entrada mal escrita o con un origen no permitido se omite.
/// </summary>
public sealed record SignedIndex(DateTimeOffset Generated, IReadOnlyList<IndexEntry> Entries)
{
    public const int CurrentFormatVersion = 1;

    /// <summary>Lo más que puede ocupar <c>indice.json</c>.</summary>
    public const int MaxBytes = 4 << 20;

    /// <summary>Lo más que puede ocupar un archivo del índice (un asset de GitHub no pasa de 2 GiB; los modelos de Hugging Face, sí).</summary>
    public const long MaxEntrySize = 64L << 30;

    /// <summary>La versión más nueva de <paramref name="id"/> que funciona con <paramref name="appVersion"/>.</summary>
    public IndexEntry? Latest(string id, PackageVersion appVersion, IndexEntryKind? kind = null) =>
        Entries.Where(e => e.Id == id && (kind is null || e.Kind == kind) && e.WorksWith(appVersion))
            .MaxBy(e => e.Version);

    /// <summary>La más nueva de <paramref name="id"/> aunque pida otra versión del programa (para avisar «necesita Rodaje x»).</summary>
    public IndexEntry? Newest(string id) => Entries.Where(e => e.Id == id).MaxBy(e => e.Version);

    /// <summary>Comprueba la firma y lee el índice.</summary>
    /// <exception cref="DownloadVerificationException">La firma no corresponde a estos bytes con esta clave.</exception>
    /// <exception cref="FormatException">Firmado, pero no es un índice que este programa entienda.</exception>
    public static SignedIndex Load(byte[] json, byte[] signature, ECDsa publicKey, DownloadUrlPolicy policy, List<string>? problems = null)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(signature);
        ArgumentNullException.ThrowIfNull(policy);
        if (json.Length > MaxBytes || !IndexSignature.Verify(json, signature, publicKey))
        {
            throw new DownloadVerificationException("El índice de complementos no tiene una firma válida de Dathinel; no se usó.");
        }

        return Parse(json, policy, problems);
    }

    private static SignedIndex Parse(byte[] json, DownloadUrlPolicy policy, List<string>? problems)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(SafeText.DecodeStrict(json));
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("formatVersion", out JsonElement format)
                || format.ValueKind != JsonValueKind.Number || !format.TryGetInt32(out int version))
            {
                throw new FormatException("El índice debe ser un objeto con formatVersion.");
            }

            if (version > CurrentFormatVersion)
            {
                throw new FormatException($"El índice usa el formato {version}; esta versión de Rodaje entiende hasta el {CurrentFormatVersion}. Actualiza Rodaje.");
            }

            DateTimeOffset generated = Str(root, "generated") is { } g
                && DateTimeOffset.TryParse(g, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed) ? parsed : DateTimeOffset.MinValue;

            var entries = new List<IndexEntry>();
            if (root.TryGetProperty("entries", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement e in list.EnumerateArray())
                {
                    if (ParseEntry(e, policy, out string? problem) is { } entry)
                    {
                        if (entries.Any(x => x.Id == entry.Id && x.Version.Equals(entry.Version) && x.Kind == entry.Kind))
                        {
                            problems?.Add($"La entrada «{entry.Id}» {entry.Version} está repetida en el índice.");
                            continue;
                        }

                        entries.Add(entry);
                    }
                    else
                    {
                        problems?.Add(problem!);
                    }
                }
            }

            return new SignedIndex(generated, entries);
        }
        catch (JsonException ex)
        {
            throw new FormatException("El índice no es JSON válido: " + ex.Message, ex);
        }
        catch (DecoderFallbackException ex)
        {
            throw new FormatException("El índice no es UTF-8 válido.", ex);
        }
        catch (InvalidOperationException ex)
        {
            // System.Text.Json la lanza al leer un texto o un nombre con un sustituto suelto escrito como escape (\ud800):
            // como el UTF-8 roto, hace inválido el índice entero.
            throw new FormatException("El índice trae un texto que no es Unicode válido.", ex);
        }
    }

    private static IndexEntry? ParseEntry(JsonElement e, DownloadUrlPolicy policy, out string? problem)
    {
        problem = null;
        string? id = e.ValueKind == JsonValueKind.Object ? Str(e, "id") : null;
        string label = id is not null && Identifiers.IsValid(id) ? id : "?";
        IndexEntryKind? kind = e.ValueKind == JsonValueKind.Object ? Str(e, "kind") switch
        {
            "content" or null => IndexEntryKind.Content,
            "module" => IndexEntryKind.Module,
            "model" => IndexEntryKind.Model,
            "app" => IndexEntryKind.App,
            _ => null,
        } : null;
        string? sha = e.ValueKind == JsonValueKind.Object ? Str(e, "sha256")?.ToLowerInvariant() : null;
        if (id is null || !Identifiers.IsValid(id) || kind is null
            || !PackageVersion.TryParse(Str(e, "version"), out PackageVersion? version)
            || sha is null || !IsSha256(sha)
            || !e.TryGetProperty("size", out JsonElement sizeElement) || sizeElement.ValueKind != JsonValueKind.Number || !sizeElement.TryGetInt64(out long size) || size <= 0 || size > MaxEntrySize
            || Str(e, "url") is not { } urlText || !Uri.TryCreate(urlText, UriKind.Absolute, out Uri? url))
        {
            problem = $"La entrada «{label}» del índice está incompleta o mal escrita.";
            return null;
        }

        if (!policy.IsAllowed(url, out string? reason))
        {
            problem = $"La entrada «{label}» del índice no se puede descargar: {reason}";
            return null;
        }

        // Un minApp que no es texto (2) no es «sin mínimo»: está mal escrito, como uno de texto ilegible.
        PackageVersion? minApp = null;
        if ((e.TryGetProperty("minApp", out JsonElement minElement) && minElement.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
            || (Str(e, "minApp") is { } min && !PackageVersion.TryParse(min, out minApp)))
        {
            problem = $"La entrada «{label}» del índice tiene un minApp mal escrito.";
            return null;
        }

        return new IndexEntry(id, version, kind.Value, url, sha, size, minApp);
    }

    /// <summary>64 caracteres hexadecimales en minúsculas.</summary>
    public static bool IsSha256(string? text) => text is { Length: 64 } && text.All(char.IsAsciiHexDigitLower);

    private static string? Str(JsonElement e, string property) =>
        e.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString()!.Trim() : null;
}
