using System.Text.Json;

namespace Rodaje.Packages.Download;

/// <summary>
/// Un modelo que declara un módulo en <c>module.models</c>: dónde va dentro de su carpeta (<c>File</c>, relativa y con
/// <c>/</c>, donde el módulo lo busca) y de dónde se baja.
/// </summary>
public sealed record ModuleModel(string Id, string File, long Size, string Sha256, Uri Url, string? License)
{
    public ModelSpec Spec => new(Id, Url, Sha256, Size);

    /// <summary>La ruta del modelo dentro de <paramref name="moduleDirectory"/>.</summary>
    public string PathIn(string moduleDirectory) => Path.GetFullPath(Path.Combine(moduleDirectory, File.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>Está en la carpeta del módulo con su tamaño exacto (el módulo comprueba la huella al abrirlo).</summary>
    public bool IsPresentIn(string moduleDirectory)
    {
        var info = new FileInfo(PathIn(moduleDirectory));
        return info.Exists && info.Length == Size;
    }
}

/// <summary>Lee <c>module.models</c> de un <c>paquete.json</c>. Lo mal escrito se omite con un aviso; nunca lanza.</summary>
public static class ModuleModels
{
    public const int MaxModels = 256;

    public static IReadOnlyList<ModuleModel> Read(string manifestPath, List<string>? problems = null)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(SafeText.ReadAllTextStrict(manifestPath),
                new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 32 });
            return Parse(document.RootElement, problems);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or System.Text.DecoderFallbackException)
        {
            problems?.Add($"{manifestPath}: {ex.Message}");
            return [];
        }
    }

    public static IReadOnlyList<ModuleModel> Parse(JsonElement root, List<string>? problems = null)
    {
        // Un texto con un sustituto suelto escapado (\ud800) haría lanzar a GetString más abajo.
        if (SafeText.HasUnreadableString(root))
        {
            problems?.Add("La lista de modelos tiene un texto que no es Unicode válido; se omite.");
            return [];
        }

        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("module", out JsonElement module) || module.ValueKind != JsonValueKind.Object
            || !module.TryGetProperty("models", out JsonElement list) || list.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var models = new List<ModuleModel>();
        foreach (JsonElement e in list.EnumerateArray())
        {
            if (models.Count == MaxModels)
            {
                problems?.Add($"Más de {MaxModels} modelos; el resto se omite.");
                break;
            }

            string? id = Str(e, "id");
            string? sha = Str(e, "sha256")?.ToLowerInvariant();
            if (id is null || !Identifiers.IsValid(id)
                || !SafePaths.TryNormalizeRelative(Str(e, "file"), out string? file)
                || sha is null || !SignedIndex.IsSha256(sha)
                || !e.TryGetProperty("size", out JsonElement size) || size.ValueKind != JsonValueKind.Number || !size.TryGetInt64(out long bytes) || bytes <= 0 || bytes > SignedIndex.MaxEntrySize
                || Str(e, "url") is not { } url || !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
            {
                problems?.Add($"El modelo «{id ?? "?"}» está incompleto o mal escrito; se omite.");
                continue;
            }

            if (models.Any(m => string.Equals(m.File, file, StringComparison.OrdinalIgnoreCase)))
            {
                problems?.Add($"El modelo «{id}» repite el archivo {file}; se omite.");
                continue;
            }

            models.Add(new ModuleModel(id, file, bytes, sha, uri, Str(e, "license")));
        }

        return models;
    }

    private static string? Str(JsonElement e, string property) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!.Trim()
            : null;
}
