namespace Rodaje.App.Services;

/// <summary>
/// Dónde guarda Rodaje lo que genera (audio limpio, voz, ProRes de CinemaDNG, máscaras): la carpeta que eligió la persona
/// o, por defecto, <c>Nombre.medios</c> junto al proyecto; sin guardar, en los datos de la app (D-034).
/// </summary>
public static class GeneratedMedia
{
    public const string FolderSuffix = ".medios";

    public static string DefaultFolder(string? projectFile) =>
        projectFile is { Length: > 0 } && Path.GetDirectoryName(Path.GetFullPath(projectFile)) is { } dir
            ? Path.Combine(dir, Path.GetFileNameWithoutExtension(projectFile) + FolderSuffix)
            : Path.Combine(AppDataPaths.Root, "medios-generados");

    /// <summary>La carpeta que toca, sin crearla. Una elegida que no sea una ruta completa se ignora.</summary>
    public static string Folder(string? projectFile, string? chosen) =>
        IsChosen(chosen) ? Path.GetFullPath(chosen!) : DefaultFolder(projectFile);

    /// <summary>Como <see cref="Folder"/>, y la crea si falta.</summary>
    public static string Ensure(string? projectFile, string? chosen)
    {
        string folder = Folder(projectFile, chosen);
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>Texto para la interfaz: «Junto al proyecto (Nombre.medios)» o la ruta elegida.</summary>
    public static string Describe(string? projectFile, string? chosen) =>
        IsChosen(chosen)
            ? Path.GetFullPath(chosen!)
            : projectFile is { Length: > 0 }
                ? $"Junto al proyecto ({Path.GetFileNameWithoutExtension(projectFile)}{FolderSuffix})"
                : "En los datos de Rodaje hasta que guardes el proyecto";

    public static bool IsChosen(string? chosen) => chosen is { Length: > 0 } && Path.IsPathFullyQualified(chosen);
}
