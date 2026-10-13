using Rodaje.Core.Model;
using Rodaje.Core.Serialization;

namespace Rodaje.App.Services;

/// <summary>
/// En memoria, las rutas de los medios son siempre absolutas: el proyecto sin guardar no tiene carpeta y el motor no
/// depende de ella. En el archivo van relativas a su carpeta, para poder mover la carpeta entera.
/// </summary>
internal static class ProjectPaths
{
    public static bool IsProjectFile(string path) =>
        string.Equals(Path.GetExtension(path), ProjectSerializer.FileExtension, StringComparison.OrdinalIgnoreCase);

    /// <summary>Resuelve las rutas relativas del proyecto recién leído contra la carpeta de su archivo.</summary>
    public static void MakeAbsolute(Project project, string projectDirectory) =>
        ProjectFilePaths.MakeAbsolute(project, projectDirectory);

    /// <summary>
    /// Copia para guardar en <paramref name="projectDirectory"/>, con las rutas relativas a esa carpeta cuando se puede: las
    /// de los medios y las de los archivos sueltos (<c>file:</c>) de las LUT, del barrido y de las máscaras de imagen.
    /// </summary>
    /// <param name="project">El proyecto en memoria; no se modifica.</param>
    /// <param name="projectDirectory">Carpeta donde se va a guardar.</param>
    /// <param name="currentDirectory">Carpeta del archivo abierto hasta ahora (las referencias <c>file:</c> relativas lo son a ella), o <c>null</c>.</param>
    public static Project ForSaving(Project project, string projectDirectory, string? currentDirectory = null) =>
        ProjectFilePaths.ForSaving(project, projectDirectory, currentDirectory);

    /// <summary>El nombre del archivo con la extensión de proyecto, aunque se haya escrito sin ella.</summary>
    public static string WithProjectExtension(string path) =>
        IsProjectFile(path) ? path : path + ProjectSerializer.FileExtension;
}
