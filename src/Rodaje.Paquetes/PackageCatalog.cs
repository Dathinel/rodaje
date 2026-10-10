using System.Text.Json;

namespace Rodaje.Paquetes;

/// <summary>Un programa que puede usar paquetes, con su nombre para mostrar; si <see cref="Planned"/>, todavía no existe.</summary>
public sealed record CatalogApp(string Id, string Name, bool Planned);

/// <summary>
/// Un paquete o módulo conocido, esté instalado o no. <see cref="Provides"/> son los tipos de elemento que trae o las
/// funciones de un módulo, solo para mostrar.
/// </summary>
public sealed record CatalogEntry(
    string Id,
    PackageKind Kind,
    string Name,
    string Description,
    Uri Repository,
    IReadOnlyList<string> Apps,
    IReadOnlyList<string> Provides);

/// <summary>
/// Catálogo de complementos conocidos (<c>catalogo.json</c>): qué existe, dónde vive su repositorio y en qué programas
/// funciona. La instalación siempre valida el manifiesto del paquete; el catálogo solo informa.
/// </summary>
public sealed record PackageCatalog(IReadOnlyList<CatalogApp> Apps, IReadOnlyList<CatalogEntry> Entries)
{
    public const int CurrentFormatVersion = 1;

    public static readonly PackageCatalog Empty = new([], []);

    public CatalogEntry? Find(string id) => Entries.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.Ordinal));

    /// <summary>Nombre para mostrar de un programa; si no está en la lista, su id.</summary>
    public string AppName(string appId)
    {
        CatalogApp? app = Apps.FirstOrDefault(a => string.Equals(a.Id, appId, StringComparison.OrdinalIgnoreCase));
        return app is null ? appId : app.Planned ? $"{app.Name} (en planes)" : app.Name;
    }

    /// <summary>Lee el catálogo. Una entrada mal escrita se omite y se informa en <paramref name="problems"/>.</summary>
    /// <exception cref="FormatException">El JSON no es un catálogo.</exception>
    public static PackageCatalog Parse(string json, List<string>? problems = null)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("formatVersion", out JsonElement format) || !format.TryGetInt32(out int version))
            {
                throw new FormatException("El catálogo debe ser un objeto con formatVersion.");
            }

            if (version > CurrentFormatVersion)
            {
                throw new FormatException($"El catálogo usa el formato {version}; este programa entiende hasta el {CurrentFormatVersion}.");
            }

            var apps = new List<CatalogApp>();
            foreach (JsonElement app in Array(root, "apps"))
            {
                if (Str(app, "id") is { } id && Identifiers.IsValid(id) && Str(app, "name") is { } name)
                {
                    apps.Add(new CatalogApp(id, name, app.TryGetProperty("planned", out JsonElement planned) && planned.ValueKind == JsonValueKind.True));
                }
                else
                {
                    problems?.Add("Un programa del catálogo no tiene id o nombre válidos.");
                }
            }

            var entries = new List<CatalogEntry>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonElement e in Array(root, "entries"))
            {
                string? id = Str(e, "id");
                string? name = Str(e, "name");
                string? repository = Str(e, "repository");
                PackageKind? kind = Str(e, "kind") switch
                {
                    "content" or null => PackageKind.Content,
                    "module" => PackageKind.Module,
                    _ => null,
                };
                if (id is null || !Identifiers.IsValid(id) || name is null || kind is null || repository is null
                    || !Uri.TryCreate(repository, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps)
                {
                    problems?.Add($"La entrada «{id ?? "?"}» del catálogo está incompleta o mal escrita.");
                    continue;
                }

                if (!ids.Add(id))
                {
                    problems?.Add($"La entrada «{id}» está repetida en el catálogo.");
                    continue;
                }

                entries.Add(new CatalogEntry(id, kind.Value, name, Str(e, "description") ?? "", uri, Strings(e, "apps"), Strings(e, "provides")));
            }

            return new PackageCatalog(apps, entries);
        }
        catch (JsonException ex)
        {
            throw new FormatException("El catálogo no es JSON válido: " + ex.Message, ex);
        }
    }

    private static List<JsonElement> Array(JsonElement e, string property) =>
        e.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object).ToList()
            : [];

    private static string? Str(JsonElement e, string property) =>
        e.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;

    private static List<string> Strings(JsonElement e, string property) =>
        e.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.Array
            ? [.. value.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!.Trim()).Where(s => s.Length > 0)]
            : [];
}

/// <summary>
/// Trae el <c>.paquete</c> de una entrada del catálogo a un archivo local, con su hash si el origen lo publica. La
/// descarga desde las releases de GitHub llega en la fase 7; hasta entonces se usa <see cref="UnavailablePackageDownloader"/>.
/// </summary>
public interface IPackageDownloader
{
    bool IsAvailable { get; }

    /// <summary>Por qué no se puede descargar, para mostrarlo.</summary>
    string? UnavailableReason { get; }

    /// <returns>La ruta del archivo descargado y su SHA-256 esperado (o <c>null</c> si el origen no lo publica).</returns>
    Task<DownloadedPackage> DownloadAsync(CatalogEntry entry, IProgress<double>? progress, CancellationToken cancellationToken);
}

public sealed record DownloadedPackage(string Path, string? ExpectedSha256);

/// <summary>Sin descarga todavía.</summary>
public sealed class UnavailablePackageDownloader(string reason) : IPackageDownloader
{
    public bool IsAvailable => false;

    public string? UnavailableReason { get; } = reason;

    public Task<DownloadedPackage> DownloadAsync(CatalogEntry entry, IProgress<double>? progress, CancellationToken cancellationToken) =>
        Task.FromException<DownloadedPackage>(new NotSupportedException(UnavailableReason));
}
