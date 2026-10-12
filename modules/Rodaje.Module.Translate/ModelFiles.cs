using System.Text.Json;
using Rodaje.Modules.Protocol;

namespace Rodaje.Module.Translate;

/// <summary>Dónde está el modelo del traductor.</summary>
internal static class ModelFiles
{
    public const string ModelFile = "Qwen3-4B-Instruct-2507-Q4_K_M.gguf";

    /// <summary>Lo que ve quien traduce cuando el modelo no está: se instala desde la página de complementos.</summary>
    public const string MissingMessage = "Falta el modelo del traductor (" + ModelFile + "): instálalo desde Complementos (IA → Traducir subtítulos).";

    /// <summary>
    /// El modelo pedido en <c>model</c> (si se pide uno y no existe, es un error: no se busca otro) o el que encuentre
    /// <see cref="Search"/>.
    /// </summary>
    public static string Find(string? requested)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            return File.Exists(requested)
                ? requested
                : throw new ModuleCallException(ErrorCodes.InvalidParameters, $"Falta el modelo del traductor en {requested}.");
        }

        return Search() ?? throw new ModuleCallException(ErrorCodes.InvalidParameters, MissingMessage);
    }

    /// <summary>
    /// La ruta del modelo instalado, o <c>null</c>. Primero por su nombre en <c>modelos\</c> junto al exe, en
    /// <c>RODAJE_MODELOS</c>, en <c>RODAJE_ENTORNO\modelos</c> o, al correr desde el repositorio, en <c>.entorno\modelos</c>
    /// de algún directorio superior (también el del repositorio principal desde un worktree de <c>.claude\worktrees</c>).
    /// Después, por su SHA-256, en el almacén de modelos compartidos, como lo deja el gestor de complementos
    /// (<c>ModelStore</c>: <c>complementos\modelos\&lt;sha256&gt;</c>, con el tamaño exacto) y como lo trae la
    /// distribución completa (<c>app\modelos\&lt;sha256&gt;</c>); el hash y el tamaño salen del <c>paquete.json</c>
    /// instalado junto al exe.
    /// </summary>
    public static string? Search()
    {
        string? byName = NamedFolders().Select(f => Path.Combine(f, ModelFile)).FirstOrDefault(File.Exists);
        if (byName is not null)
        {
            return byName;
        }

        return ReadModelHash(Path.Combine(AppContext.BaseDirectory, "paquete.json")) is { } model
            ? FindByHash(StoreFolders(), model.Sha256, model.Size)
            : null;
    }

    private static List<string> NamedFolders()
    {
        List<string> folders = [Path.Combine(AppContext.BaseDirectory, "modelos")];
        if (Environment.GetEnvironmentVariable("RODAJE_MODELOS") is { Length: > 0 } modelos)
        {
            folders.Add(modelos);
        }

        if (Environment.GetEnvironmentVariable("RODAJE_ENTORNO") is { Length: > 0 } entorno)
        {
            folders.Add(Path.Combine(entorno, "modelos"));
        }

        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            folders.Add(Path.Combine(d.FullName, ".entorno", "modelos"));
            if (d.Name == "worktrees" && d.Parent?.Name == ".claude" && d.Parent.Parent is { } repo)
            {
                folders.Add(Path.Combine(repo.FullName, ".entorno", "modelos"));
            }
        }

        return folders;
    }

    /// <summary>
    /// Las carpetas donde los modelos se guardan por hash: dos niveles por encima del exe (el módulo vive en
    /// <c>complementos\modulos\&lt;id&gt;\</c> o en <c>app\modulos\&lt;id&gt;\</c>, así que es <c>complementos\modelos</c>
    /// o <c>app\modelos</c>) y la de los datos de la app (<c>RODAJE_DATOS</c> o <c>%LOCALAPPDATA%\Rodaje</c>), para un
    /// módulo de la distribución completa con el modelo bajado desde el gestor.
    /// </summary>
    internal static List<string> StoreFolders()
    {
        List<string> folders = [];
        if (new DirectoryInfo(AppContext.BaseDirectory).Parent?.Parent is { } modulesParent)
        {
            folders.Add(Path.Combine(modulesParent.FullName, "modelos"));
        }

        string data = Environment.GetEnvironmentVariable("RODAJE_DATOS") is { Length: > 0 } custom && !string.IsNullOrWhiteSpace(custom)
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Rodaje");
        folders.Add(Path.Combine(data, "complementos", "modelos"));
        return folders;
    }

    /// <summary>
    /// Como <c>ModelStore.Find</c>: un archivo con el nombre de su SHA-256 lo dejó el descargador después de comprobarlo;
    /// basta con que exista y tenga el tamaño exacto.
    /// </summary>
    internal static string? FindByHash(IEnumerable<string> folders, string sha256, long size) =>
        folders.Select(f => Path.Combine(f, sha256)).FirstOrDefault(p => File.Exists(p) && new FileInfo(p).Length == size);

    /// <summary>
    /// El SHA-256 y el tamaño del modelo en <c>module.models</c> del manifiesto (la entrada cuyo <c>file</c> termina en
    /// <see cref="ModelFile"/>), o <c>null</c> si falta el manifiesto o no los trae válidos.
    /// </summary>
    internal static (string Sha256, long Size)? ReadModelHash(string manifestPath)
    {
        try
        {
            if (!File.Exists(manifestPath) || new FileInfo(manifestPath).Length > 1 << 20)
            {
                return null;
            }

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifestPath),
                new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (!document.RootElement.TryGetProperty("module", out JsonElement module) || module.ValueKind != JsonValueKind.Object
                || !module.TryGetProperty("models", out JsonElement models) || models.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (JsonElement model in models.EnumerateArray())
            {
                if (model.ValueKind == JsonValueKind.Object
                    && model.TryGetProperty("file", out JsonElement file) && file.ValueKind == JsonValueKind.String
                    && file.GetString()!.Replace('\\', '/').EndsWith("/" + ModelFile, StringComparison.Ordinal)
                    && model.TryGetProperty("sha256", out JsonElement sha) && sha.ValueKind == JsonValueKind.String
                    && sha.GetString() is { Length: 64 } hash && hash.All(char.IsAsciiHexDigitLower)
                    && model.TryGetProperty("size", out JsonElement size) && size.ValueKind == JsonValueKind.Number
                    && size.TryGetInt64(out long bytes) && bytes > 0)
                {
                    return (hash, bytes);
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}
