using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Views.Library;

/// <summary>Tipo de medio por el que se puede filtrar la biblioteca.</summary>
public enum LibraryKind
{
    All,
    Video,
    Image,
    Audio,
}

/// <summary>Lo que se ve en la biblioteca de medios: la carpeta abierta, el texto buscado, el tipo y si solo favoritos.</summary>
public sealed record LibraryQuery(string? FolderId = null, string Text = "", LibraryKind Kind = LibraryKind.All, bool FavoritesOnly = false)
{
    /// <summary>
    /// Buscar o pedir solo favoritos mira toda la biblioteca, no solo la carpeta abierta (como CapCut): lo que se
    /// busca aparece aunque esté en otra carpeta.
    /// </summary>
    public bool IsGlobal => !string.IsNullOrWhiteSpace(Text) || FavoritesOnly;
}

/// <summary>Filtro y búsqueda de la biblioteca, sin WinUI.</summary>
public static class LibraryFilter
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".tif", ".tiff", ".heic", ".heif", ".avif", ".jxl", ".tga", ".dpx", ".exr",
    };

    /// <summary>Los tipos del filtro con su nombre visible, en el orden del desplegable.</summary>
    public static IReadOnlyList<(LibraryKind Kind, string Label)> Kinds { get; } =
    [
        (LibraryKind.All, "Todo"),
        (LibraryKind.Video, "Videos"),
        (LibraryKind.Image, "Imágenes"),
        (LibraryKind.Audio, "Audio"),
    ];

    /// <summary>Más corto que esto, sin tasa de cuadros y sin sonido, es una imagen fija aunque su extensión no lo diga.</summary>
    private static readonly Time StillDuration = Time.FromSeconds(1);

    /// <summary>
    /// Audio si no tiene imagen; imagen si tiene extensión de imagen, o si no tiene tasa de cuadros ni sonido y dura menos
    /// de un segundo (una foto en un formato raro); si no, video. Un video al que el lector no le dio tasa de cuadros
    /// (algunos de tasa variable) sigue siendo video.
    /// </summary>
    public static LibraryKind KindOf(MediaRef media)
    {
        ArgumentNullException.ThrowIfNull(media);
        if (!media.HasVideo)
        {
            return LibraryKind.Audio;
        }

        if (ImageExtensions.Contains(System.IO.Path.GetExtension(media.Path)))
        {
            return LibraryKind.Image;
        }

        return media.FrameRate is null && !media.HasAudio && media.Duration < StillDuration ? LibraryKind.Image : LibraryKind.Video;
    }

    /// <summary>Los medios que se ven con <paramref name="query"/>, en el orden del proyecto.</summary>
    public static IReadOnlyList<MediaRef> Apply(IEnumerable<MediaRef> media, LibraryOrganization organization, LibraryQuery query)
    {
        ArgumentNullException.ThrowIfNull(media);
        ArgumentNullException.ThrowIfNull(organization);
        ArgumentNullException.ThrowIfNull(query);
        string? folder = organization.FindFolder(query.FolderId)?.Id;
        string[] words = SearchText.Words(query.Text);
        return [.. media.Where(m =>
            (query.IsGlobal || organization.FolderOf(m.Id) == folder)
            && (query.Kind == LibraryKind.All || KindOf(m) == query.Kind)
            && (!query.FavoritesOnly || organization.IsFavorite(m.Id))
            && Matches(m, words))];
    }

    /// <summary>Las carpetas que se ven arriba de los medios: las de la carpeta abierta, o ninguna al buscar.</summary>
    public static IReadOnlyList<LibraryFolder> Folders(LibraryOrganization organization, LibraryQuery query)
    {
        ArgumentNullException.ThrowIfNull(organization);
        ArgumentNullException.ThrowIfNull(query);
        return query.IsGlobal ? [] : organization.Children(query.FolderId);
    }

    /// <summary>«3 medios», «1 medio · 2 carpetas», «Vacía»: lo que hay directamente en la carpeta, para su ficha.</summary>
    public static string FolderSummary(int media, int folders)
    {
        var parts = new List<string>();
        if (media > 0)
        {
            parts.Add(media == 1 ? "1 medio" : $"{media} medios");
        }

        if (folders > 0)
        {
            parts.Add(folders == 1 ? "1 carpeta" : $"{folders} carpetas");
        }

        return parts.Count == 0 ? "Vacía" : string.Join(" · ", parts);
    }

    /// <summary>Cuántos medios de <paramref name="media"/> están directamente en la carpeta.</summary>
    public static int CountIn(IEnumerable<MediaRef> media, LibraryOrganization organization, string folderId)
    {
        ArgumentNullException.ThrowIfNull(media);
        ArgumentNullException.ThrowIfNull(organization);
        return media.Count(m => organization.FolderOf(m.Id) == folderId);
    }

    /// <summary>Cada palabra buscada aparece en el nombre o en el nombre del archivo, sin mirar mayúsculas ni tildes.</summary>
    public static bool Matches(MediaRef media, IReadOnlyList<string> words)
    {
        ArgumentNullException.ThrowIfNull(media);
        ArgumentNullException.ThrowIfNull(words);
        if (words.Count == 0)
        {
            return true;
        }

        string haystack = SearchText.Fold(media.Name) + "\n" + SearchText.Fold(System.IO.Path.GetFileName(media.Path));
        return words.All(w => haystack.Contains(w, StringComparison.Ordinal));
    }

    /// <summary>Texto vacío para la cabecera, o por qué no se ve nada con el filtro puesto.</summary>
    public static string EmptyMessage(LibraryQuery query, bool folderHasContent)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!string.IsNullOrWhiteSpace(query.Text))
        {
            return $"Nada coincide con «{query.Text.Trim()}».";
        }

        if (query.FavoritesOnly)
        {
            return "Aún no hay favoritos: marca un medio con la estrella de su menú.";
        }

        if (query.Kind != LibraryKind.All && folderHasContent)
        {
            return "No hay medios de este tipo aquí.";
        }

        return query.FolderId is null ? "" : "Carpeta vacía: arrastra medios hasta aquí o a su ficha.";
    }
}

/// <summary>
/// Texto para buscar sin mirar mayúsculas ni tildes («Montaña» encuentra «montana»). La app corre con globalización
/// invariante, así que no se usa la comparación de la cultura: se pliegan a mano las letras latinas con tilde y se quitan
/// las marcas combinantes, que es como llegan las tildes en un texto descompuesto (NFD, por ejemplo nombres de archivo
/// copiados de un Mac: «a» seguida de U+0301).
/// </summary>
public static class SearchText
{
    private const string Accented = "áàâäãåāăąéèêëēėęěíìîïīįóòôöõøōőúùûüūůűųýÿçćčďđğłńñňřśšşťźżžæœß";
    private const string Plain = "aaaaaaaaa" + "eeeeeeee" + "iiiiii" + "oooooooo" + "uuuuuuuu" + "yy" + "ccc" + "dd" + "g" + "l" + "nnn" + "r" + "sss" + "t" + "zzz";

    public static string Fold(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        var builder = new System.Text.StringBuilder(text.Length);
        foreach (char raw in text)
        {
            if (IsCombiningMark(raw))
            {
                continue;
            }

            char c = char.ToLowerInvariant(raw);
            int index = Accented.IndexOf(c, StringComparison.Ordinal);
            if (index < 0)
            {
                builder.Append(c);
            }
            else if (index < Plain.Length)
            {
                builder.Append(Plain[index]);
            }
            else
            {
                // æ, œ y ß se escriben con dos letras.
                builder.Append(c switch { 'æ' => "ae", 'œ' => "oe", _ => "ss" });
            }
        }

        return builder.ToString();
    }

    /// <summary>Marcas diacríticas combinantes (los bloques de Unicode que solo llevan tildes y signos sobre la letra).</summary>
    private static bool IsCombiningMark(char c) =>
        c is (>= '\u0300' and <= '\u036F') or (>= '\u1AB0' and <= '\u1AFF') or (>= '\u1DC0' and <= '\u1DFF')
            or (>= '\u20D0' and <= '\u20FF') or (>= '\uFE20' and <= '\uFE2F');

    /// <summary>Las palabras del texto buscado, ya plegadas.</summary>
    public static string[] Words(string? text) =>
        Fold(text).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
