using Rodaje.Core.Editing;
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
        string safe = SafeId(media);
        return MediaFolder + Path.DirectorySeparatorChar + (safe.Length > 0 ? safe : "m" + index);
    }

    /// <summary>El medio se queda con el nombre que trae el manifiesto de la sala cuando no tiene: así lo enseña cualquier aviso.</summary>
    public static void FillNames(Project project, IEnumerable<MediaManifestEntry> media)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(media);
        foreach (MediaManifestEntry entry in media)
        {
            if (project.FindMedia(entry.MediaId) is { } mediaRef && string.IsNullOrWhiteSpace(mediaRef.Name))
            {
                mediaRef.Name = entry.Name;
            }
        }
    }

    /// <summary>
    /// Si la ruta del medio (relativa o ya resuelta contra la carpeta del proyecto) es la de <see cref="PlaceholderFor"/>:
    /// entonces su último tramo es el id interno, que nunca se enseña.
    /// </summary>
    public static bool IsPlaceholder(MediaRef media)
    {
        ArgumentNullException.ThrowIfNull(media);
        string path = media.Path;
        int cut = path.AsSpan().LastIndexOfAny('\\', '/');
        if (cut < 0)
        {
            return false;
        }

        string file = path[(cut + 1)..];
        string parent = MediaRelinkSearch.FileName(path[..cut]);
        string safe = SafeId(media);
        return string.Equals(parent, MediaFolder, StringComparison.OrdinalIgnoreCase)
            && (safe.Length > 0 ? file == safe : file.Length > 1 && file[0] == 'm' && file.Skip(1).All(char.IsAsciiDigit));
    }

    private static string SafeId(MediaRef media) => new([.. media.Id.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_').Take(48)]);
}
