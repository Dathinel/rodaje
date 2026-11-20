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
}
