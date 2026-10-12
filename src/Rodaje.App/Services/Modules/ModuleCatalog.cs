using System.Text.Json;
using Rodaje.Paquetes;

namespace Rodaje.App.Services.Modules;

/// <summary>De dónde salió un módulo.</summary>
public enum ModuleOrigin
{
    Installed,
    Bundled,

    /// <summary>La carpeta <c>modules/</c> del repositorio, solo al correr Rodaje desde su código.</summary>
    Development,
}

/// <summary>
/// Un módulo encontrado en disco, con lo que hace falta para abrirlo. <see cref="VramMb"/> es lo que declara
/// <c>module.vramMb</c> (0 = solo CPU); el <see cref="ModuleManager"/> reparte la memoria de video con eso.
/// </summary>
public sealed record ModuleDescriptor(
    string Id,
    string Name,
    string Version,
    string Directory,
    string EntryPath,
    IReadOnlyList<string> Functions,
    int VramMb,
    string? Runtime,
    ModuleOrigin Origin)
{
    public bool UsesGpu => VramMb > 0;

    public bool Offers(string function) => Functions.Contains(function, StringComparer.Ordinal);
}

/// <summary>Una carpeta donde buscar módulos (cada subcarpeta con un <c>paquete.json</c> de tipo módulo).</summary>
public sealed record ModuleRoot(string Path, ModuleOrigin Origin);

/// <summary>Busca los módulos instalados. Si un id aparece en varias carpetas, gana la versión mayor y, a igual versión, la primera carpeta.</summary>
public static class ModuleCatalog
{
    public const string AppId = "rodaje";

    /// <summary>Hasta dónde se aceptan valores de <c>vramMb</c> (64 GB).</summary>
    public const int MaxVramMb = 65_536;

    /// <summary>
    /// Instalados (<c>%LOCALAPPDATA%\Rodaje\complementos\modulos</c>), los que trae la distribución completa
    /// (<c>modulos\</c> junto al exe) y, si Rodaje corre desde el repositorio, su carpeta <c>modules\</c>.
    /// </summary>
    public static IReadOnlyList<ModuleRoot> DefaultRoots(string? baseDirectory = null)
    {
        baseDirectory ??= AppContext.BaseDirectory;
        List<ModuleRoot> roots =
        [
            new(Path.Combine(AppDataPaths.Root, "complementos", "modulos"), ModuleOrigin.Installed),
            new(Path.Combine(baseDirectory, "modulos"), ModuleOrigin.Bundled),
        ];
        if (FindDevelopmentModules(baseDirectory) is { } dev)
        {
            roots.Add(new ModuleRoot(dev, ModuleOrigin.Development));
        }

        return roots;
    }

    /// <summary>La carpeta <c>modules</c> del repositorio que contiene a <paramref name="baseDirectory"/>, o <c>null</c>.</summary>
    public static string? FindDevelopmentModules(string baseDirectory)
    {
        for (DirectoryInfo? dir = new(baseDirectory); dir is not null; dir = dir.Parent)
        {
            string modules = Path.Combine(dir.FullName, "modules");
            if (Directory.Exists(modules) && File.Exists(Path.Combine(dir.FullName, "Rodaje.slnx")))
            {
                return modules;
            }
        }

        return null;
    }

    public static IReadOnlyList<ModuleDescriptor> Scan(IEnumerable<ModuleRoot> roots, List<string>? problems = null)
    {
        ArgumentNullException.ThrowIfNull(roots);
        var found = new Dictionary<string, (ModuleDescriptor Module, PackageVersion Version)>(StringComparer.Ordinal);
        foreach (ModuleRoot root in roots)
        {
            foreach (string directory in Subdirectories(root.Path, problems))
            {
                if (Read(directory, root.Origin, problems) is not { } read)
                {
                    continue;
                }

                if (!found.TryGetValue(read.Module.Id, out var existing) || read.Version.CompareTo(existing.Version) > 0)
                {
                    found[read.Module.Id] = read;
                }
            }
        }

        return [.. found.Values.Select(v => v.Module).OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    private static string[] Subdirectories(string root, List<string>? problems)
    {
        try
        {
            return Directory.Exists(root) ? Directory.GetDirectories(root) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problems?.Add($"{root}: {ex.Message}");
            return [];
        }
    }

    private static (ModuleDescriptor Module, PackageVersion Version)? Read(string directory, ModuleOrigin origin, List<string>? problems)
    {
        string manifestPath = Path.Combine(directory, PackageManifest.FileName);
        if (!File.Exists(manifestPath) || Path.GetFileName(directory).StartsWith('.'))
        {
            return null;
        }

        // En desarrollo el exe está en bin\<config>\…, no donde dice entry: se busca aparte.
        bool development = origin == ModuleOrigin.Development;
        ManifestResult result = ManifestReader.ReadFile(manifestPath, checkFiles: !development);
        if (result.Manifest is not { Kind: PackageKind.Module, Module: { } module } manifest || !manifest.WorksIn(AppId))
        {
            if (result.Errors.Count > 0)
            {
                problems?.Add($"{directory}: {string.Join(" ", result.Errors)}");
            }

            return null;
        }

        if (module.Protocol != 1)
        {
            problems?.Add($"{directory}: protocolo {module.Protocol} no admitido.");
            return null;
        }

        string? entry = Path.GetFullPath(Path.Combine(directory, module.Entry));
        if (development && !File.Exists(entry))
        {
            entry = FindBuiltExe(directory, Path.GetFileName(entry));
        }

        if (entry is null || !File.Exists(entry))
        {
            problems?.Add($"{directory}: falta {module.Entry}.");
            return null;
        }

        (int vram, string? runtime) = ReadExtras(manifestPath);
        return (new ModuleDescriptor(manifest.Id, manifest.Name, manifest.Version.ToString(), directory, entry, module.Functions, vram, runtime, origin),
            manifest.Version);
    }

    /// <summary>El exe compilado más reciente bajo <c>bin\</c> (el de Release gana a igual fecha).</summary>
    private static string? FindBuiltExe(string directory, string fileName)
    {
        string bin = Path.Combine(directory, "bin");
        if (!Directory.Exists(bin))
        {
            return null;
        }

        try
        {
            return Directory.EnumerateFiles(bin, fileName, SearchOption.AllDirectories)
                // «native» es el exe AOT a medio publicar, sin las DLL nativas que pueda necesitar al lado.
                .Where(p => !p.Contains(Path.DirectorySeparatorChar + "ref" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    && !p.Contains(Path.DirectorySeparatorChar + "native" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary><c>module.vramMb</c> y <c>module.runtime</c>, campos de la fase 7 que el lector de manifiestos aún no expone.</summary>
    private static (int VramMb, string? Runtime) ReadExtras(string manifestPath)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(manifestPath), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 32 });
            if (!doc.RootElement.TryGetProperty("module", out JsonElement module) || module.ValueKind != JsonValueKind.Object)
            {
                return (0, null);
            }

            int vram = module.TryGetProperty("vramMb", out JsonElement v) && v.TryGetInt32(out int mb) ? Math.Clamp(mb, 0, MaxVramMb) : 0;
            string? runtime = module.TryGetProperty("runtime", out JsonElement r) && r.ValueKind == JsonValueKind.String && r.GetString() is { Length: > 0 and <= 64 } s ? s : null;
            return (vram, runtime);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return (0, null);
        }
    }
}
