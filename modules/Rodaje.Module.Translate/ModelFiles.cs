using Rodaje.Modules.Protocol;

namespace Rodaje.Module.Translate;

/// <summary>Dónde está el modelo del traductor.</summary>
internal static class ModelFiles
{
    public const string ModelFile = "Qwen3-4B-Instruct-2507-Q4_K_M.gguf";

    /// <summary>
    /// El modelo pedido en <c>model</c> (si se pide uno y no existe, es un error: no se busca otro), o el primero que
    /// aparezca en <c>modelos\</c> junto al exe, en <c>RODAJE_MODELOS</c>, en <c>RODAJE_ENTORNO\modelos</c> o, al correr
    /// desde el repositorio, en <c>.entorno\modelos</c> de algún directorio superior (también el del repositorio
    /// principal desde un worktree de <c>.claude\worktrees</c>).
    /// </summary>
    public static string Find(string? requested)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            return File.Exists(requested)
                ? requested
                : throw new ModuleCallException(ErrorCodes.InvalidParameters, $"Falta el modelo del traductor en {requested}.");
        }

        return Search() ?? throw new ModuleCallException(ErrorCodes.InvalidParameters,
            $"Falta el modelo {ModelFile}: reinstala el módulo de traducción o ejecuta build\\preparar-modelos.ps1 -Traductor.");
    }

    /// <summary>La ruta del modelo instalado, o <c>null</c>.</summary>
    public static string? Search()
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

        return folders.Select(f => Path.Combine(f, ModelFile)).FirstOrDefault(File.Exists);
    }
}
