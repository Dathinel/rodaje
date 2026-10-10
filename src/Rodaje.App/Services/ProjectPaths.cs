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
    public static void MakeAbsolute(Project project, string projectDirectory)
    {
        foreach (MediaRef media in project.Media)
        {
            media.Path = media.ResolvePath(projectDirectory);
        }
    }

    /// <summary>Copia para guardar en <paramref name="projectDirectory"/>, con las rutas relativas a esa carpeta cuando se puede.</summary>
    public static Project ForSaving(Project project, string projectDirectory)
    {
        Project copy = project.Clone();
        foreach (MediaRef media in copy.Media)
        {
            if (Path.IsPathFullyQualified(media.Path))
            {
                // En otra unidad, GetRelativePath devuelve la ruta absoluta tal cual.
                media.Path = Path.GetRelativePath(projectDirectory, media.Path);
            }
        }

        return copy;
    }

    /// <summary>El nombre del archivo con la extensión de proyecto, aunque se haya escrito sin ella.</summary>
    public static string WithProjectExtension(string path) =>
        IsProjectFile(path) ? path : path + ProjectSerializer.FileExtension;
}
