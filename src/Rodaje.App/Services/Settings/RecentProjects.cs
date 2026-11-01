namespace Rodaje.App.Services.Settings;

/// <summary>Una entrada del menú Archivo › Proyectos recientes: la ruta completa y cómo se muestra.</summary>
public sealed record RecentProjectEntry(string Path, string Label);

/// <summary>Arma la lista de proyectos recientes del menú: quita los que ya no existen y abrevia las rutas largas.</summary>
public static class RecentProjects
{
    /// <summary>Lo más larga que se muestra una ruta en el menú.</summary>
    public const int MaxLabelLength = 60;

    /// <summary>
    /// Las entradas del menú en el orden de <paramref name="paths"/> (el más reciente primero), solo con los archivos que
    /// <paramref name="exists"/> confirma, sin repetidos y hasta <paramref name="max"/>.
    /// </summary>
    public static IReadOnlyList<RecentProjectEntry> Build(IEnumerable<string> paths, Func<string, bool> exists, int max = AppSettings.MaxRecentProjects)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(exists);
        var entries = new List<RecentProjectEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths)
        {
            if (entries.Count >= max)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(path) || !seen.Add(path) || !exists(path))
            {
                continue;
            }

            entries.Add(new RecentProjectEntry(path, Abbreviate(path)));
        }

        return entries;
    }

    /// <summary>
    /// La ruta como cabe en un menú: entera si es corta; si no, la raíz, «…» y las últimas carpetas que quepan junto al
    /// nombre del archivo (<c>C:\…\Bodas\Marta y Juan.rodaje</c>). El nombre del archivo se muestra siempre entero.
    /// </summary>
    public static string Abbreviate(string path, int maxLength = MaxLabelLength)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, 8);
        if (path.Length <= maxLength)
        {
            return path;
        }

        char separator = path.Contains('\\', StringComparison.Ordinal) ? '\\' : '/';
        string[] parts = path.Split(separator, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
        {
            return path;
        }

        string root = parts[0] + separator;
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            root = @"\\" + parts[0] + separator;
        }
        else if (path[0] == '/')
        {
            root = "/";
        }

        string tail = parts[^1];
        for (int first = 1; first < parts.Length; first++)
        {
            string middle = string.Join(separator, parts, first, parts.Length - first - 1);
            string candidate = root + "…" + separator + (middle.Length == 0 ? "" : middle + separator) + tail;
            if (candidate.Length <= maxLength)
            {
                return candidate;
            }
        }

        return root + "…" + separator + tail;
    }
}
