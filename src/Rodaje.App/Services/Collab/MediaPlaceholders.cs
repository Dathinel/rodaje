using Rodaje.Core.Model;

namespace Rodaje.App.Services.Collab;

/// <summary>
/// El proyecto de la sala llega sin rutas de medios (<c>ProjectWire</c> las quita): cada medio recibe una ruta relativa sin
/// significado (<c>medios\&lt;id&gt;</c>) para que el archivo guardado sea normal y el medio salga «sin conexión» en vez de
/// apuntar a la carpeta del proyecto.
/// </summary>
public static class MediaPlaceholders
{
    public const string MediaFolder = "medios";

    public static void Fill(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        for (int i = 0; i < project.Media.Count; i++)
        {
            project.Media[i].Path = PlaceholderFor(project.Media[i], i);
        }
    }

    /// <summary>Una ruta relativa que no existe ni apunta fuera de la carpeta del proyecto.</summary>
    public static string PlaceholderFor(MediaRef media, int index)
    {
        string safe = new([.. media.Id.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_').Take(48)]);
        return MediaFolder + Path.DirectorySeparatorChar + (safe.Length > 0 ? safe : "m" + index);
    }

    /// <summary>Si la ruta del medio es el marcador de la sala (<c>medios\…</c>): no hay archivo detrás en este equipo.</summary>
    public static bool IsPlaceholder(string? path) =>
        path is { Length: > 0 } p
        && (p.StartsWith(MediaFolder + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || p.StartsWith(MediaFolder + '/', StringComparison.Ordinal));

    /// <summary>
    /// Si al proyecto le faltan originales en este equipo: un medio sin ruta, con el marcador de la sala o apuntando a un
    /// archivo que ya no está. Con ellos «Exportar» no se ofrece a un invitado: para eso está «Exportar en el anfitrión».
    /// </summary>
    public static bool MissingOriginals(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return project.Media.Any(m => m.Path.Length == 0 || IsPlaceholder(m.Path)
            || (Path.IsPathFullyQualified(m.Path) && !File.Exists(m.Path)));
    }
}
