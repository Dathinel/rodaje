namespace Rodaje.Core.Model;

/// <summary>Una fila de la tabla de mapeo de rutas del usuario («D:\Material ↔ /Volumes/Material»).</summary>
public sealed record PathMapping(string From, string To);

/// <summary>Tipo de ruta según su forma léxica, sin mirar el sistema anfitrión.</summary>
public enum StoredPathKind
{
    Relative,
    WindowsAbsolute,
    PosixAbsolute,
}

/// <summary>Reconocimiento, mapeo y resolución de rutas entre plataformas.</summary>
public static class PortablePath
{
    /// <summary>
    /// Clasifica una ruta según su forma:
    /// <list type="bullet">
    /// <item><c>X:\…</c>, <c>X:/…</c>, <c>\\servidor\recurso\…</c> o <c>//servidor/recurso/…</c> → <see cref="StoredPathKind.WindowsAbsolute"/>.</item>
    /// <item>Empieza por <c>/</c> (y no es UNC como <c>//servidor/…</c>) → <see cref="StoredPathKind.PosixAbsolute"/>.</item>
    /// <item>Lo demás, incluidos <c>\Videos\a.mp4</c> y <c>C:a.mp4</c> → <see cref="StoredPathKind.Relative"/>.</item>
    /// </list>
    /// </summary>
    public static StoredPathKind Classify(string stored)
    {
        ArgumentNullException.ThrowIfNull(stored);

        if (stored.Length >= 3 && char.IsAsciiLetter(stored[0]) && stored[1] == ':' && (stored[2] == '\\' || stored[2] == '/'))
        {
            return StoredPathKind.WindowsAbsolute;
        }

        if (IsUnc(stored))
        {
            return StoredPathKind.WindowsAbsolute;
        }

        if (stored.StartsWith('/'))
        {
            if (stored.StartsWith("//", StringComparison.Ordinal) && stored.Length > 2 && stored[2] != '/')
            {
                return StoredPathKind.Relative;
            }

            return StoredPathKind.PosixAbsolute;
        }

        return StoredPathKind.Relative;
    }

    /// <summary>
    /// Aplica las filas de mapeo: la primera cuyo <c>From</c> es prefijo de <paramref name="stored"/> en un límite de
    /// carpeta cambia ese prefijo por <c>To</c> y deja el resto con el separador de <c>To</c>.
    /// </summary>
    public static string ApplyMappings(string stored, IReadOnlyList<PathMapping>? mappings)
    {
        ArgumentNullException.ThrowIfNull(stored);

        if (mappings is null || mappings.Count == 0 || stored.Length == 0)
        {
            return stored;
        }

        foreach (PathMapping mapping in mappings)
        {
            if (string.IsNullOrEmpty(mapping.From))
            {
                continue;
            }

            string from = mapping.From;
            bool fromIsWindows = Classify(from) == StoredPathKind.WindowsAbsolute;

            if (!IsPrefixOnBoundary(stored, from, fromIsWindows, out int matchLength))
            {
                continue;
            }

            string to = mapping.To ?? "";
            char targetSep;
            StoredPathKind toKind = Classify(to);
            if (toKind == StoredPathKind.PosixAbsolute)
            {
                targetSep = '/';
            }
            else if (toKind == StoredPathKind.WindowsAbsolute)
            {
                targetSep = '\\';
            }
            else
            {
                targetSep = to.Contains('\\') ? '\\' : '/';
            }

            string remainder = stored.Substring(matchLength).TrimStart('\\', '/');
            if (targetSep == '/')
            {
                remainder = remainder.Replace('\\', '/');
            }
            else
            {
                remainder = remainder.Replace('/', '\\');
            }

            if (remainder.Length == 0)
            {
                return to;
            }

            string trimmedTo = to.TrimEnd('\\', '/');
            if (trimmedTo.Length == 0 && to.Length > 0)
            {
                trimmedTo = targetSep.ToString();
            }

            if (trimmedTo.EndsWith('/') || trimmedTo.EndsWith('\\'))
            {
                return trimmedTo + remainder;
            }

            return trimmedTo + targetSep + remainder;
        }

        return stored;
    }

    /// <summary>
    /// Resuelve una ruta guardada contra la carpeta del proyecto aplicando previamente el mapeo de rutas.
    /// </summary>
    internal static string Resolve(string stored, string projectDirectory, IReadOnlyList<PathMapping>? mappings, bool hostIsWindows)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(projectDirectory);

        if (mappings is { Count: > 0 })
        {
            stored = ApplyMappings(stored, mappings);
        }

        if (hostIsWindows)
        {
            if (OperatingSystem.IsWindows())
            {
                if (System.IO.Path.IsPathFullyQualified(stored))
                {
                    return stored;
                }

                try
                {
                    return System.IO.Path.GetFullPath(stored, System.IO.Path.GetFullPath(projectDirectory));
                }
                catch (System.IO.PathTooLongException)
                {
                    return stored;
                }
            }

            return ResolveWindowsEmulated(stored, projectDirectory);
        }

        StoredPathKind kind = Classify(stored);
        if (kind == StoredPathKind.WindowsAbsolute)
        {
            return stored;
        }

        if (kind == StoredPathKind.PosixAbsolute)
        {
            return NormalizePosixPath(stored);
        }

        string relative = stored.Replace('\\', '/');
        string projectDir = projectDirectory.Replace('\\', '/');
        string combined = CombinePosix(projectDir, relative);
        return NormalizePosixPath(combined);
    }

    /// <summary>
    /// Busca un archivo segmento a segmento sin distinguir mayúsculas cuando no existe con la grafía exacta.
    /// Si hay una sola entrada coincidente por segmento devuelve la ruta real; si no existe o hay ambigüedad, devuelve <c>null</c>.
    /// </summary>
    public static string? FindIgnoringCase(string path, Func<string, bool> fileExists, Func<string, IEnumerable<string>> listDirectory)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(fileExists);
        ArgumentNullException.ThrowIfNull(listDirectory);

        if (path.Length == 0)
        {
            return null;
        }

        if (fileExists(path))
        {
            return path;
        }

        char sep = path.Contains('\\') ? '\\' : '/';
        string root;
        string remaining;

        StoredPathKind kind = Classify(path);
        if (kind == StoredPathKind.PosixAbsolute)
        {
            root = "/";
            sep = '/';
            remaining = path.Substring(1);
        }
        else if (kind == StoredPathKind.WindowsAbsolute)
        {
            if (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/'))
            {
                root = path.Substring(0, 3);
                sep = path[2];
                remaining = path.Substring(3);
            }
            else if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
            {
                int sep1 = path.IndexOfAny(['\\', '/'], 2);
                int sep2 = sep1 >= 0 ? path.IndexOfAny(['\\', '/'], sep1 + 1) : -1;
                if (sep2 >= 0)
                {
                    root = path.Substring(0, sep2 + 1);
                    sep = path[0];
                    remaining = path.Substring(sep2 + 1);
                }
                else
                {
                    root = path;
                    remaining = "";
                }
            }
            else
            {
                root = "";
                remaining = path;
            }
        }
        else
        {
            root = "";
            remaining = path;
        }

        string[] segments = remaining.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return fileExists(root) ? root : null;
        }

        string current = root;
        foreach (string segment in segments)
        {
            IEnumerable<string>? entries = listDirectory(current);
            if (entries is null && current.Length == 0)
            {
                entries = listDirectory(".");
            }

            if (entries is null)
            {
                return null;
            }

            List<string> matches = [];
            foreach (string entry in entries)
            {
                string entryName = GetEntryName(entry);
                if (string.Equals(entryName, segment, StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add(entryName);
                }
            }

            if (matches.Count != 1)
            {
                return null;
            }

            string matchedName = matches[0];
            if (current == "/")
            {
                current = "/" + matchedName;
            }
            else if (current.Length == 0)
            {
                current = matchedName;
            }
            else if (current.EndsWith('/') || current.EndsWith('\\'))
            {
                current += matchedName;
            }
            else
            {
                current = current + sep + matchedName;
            }
        }

        if ((path.EndsWith('/') || path.EndsWith('\\')) && !current.EndsWith(sep))
        {
            current += sep;
        }

        return current;
    }

    private static bool IsUnc(string stored)
    {
        if (stored.Length < 5)
        {
            return false;
        }

        bool slashSlash = stored[0] == '\\' && stored[1] == '\\';
        bool forwardSlash = stored[0] == '/' && stored[1] == '/';
        if (!slashSlash && !forwardSlash)
        {
            return false;
        }

        if (stored[2] == '\\' || stored[2] == '/')
        {
            return false;
        }

        int nextSep = stored.IndexOfAny(['\\', '/'], 2);
        if (nextSep < 0 || nextSep == stored.Length - 1)
        {
            return false;
        }

        char afterSep = stored[nextSep + 1];
        return afterSep != '\\' && afterSep != '/';
    }

    private static bool IsPrefixOnBoundary(string stored, string from, bool fromIsWindows, out int matchLength)
    {
        matchLength = 0;
        int fromLen = from.Length;
        if (stored.Length < fromLen)
        {
            return false;
        }

        if (fromIsWindows)
        {
            for (int i = 0; i < fromLen; i++)
            {
                char cf = from[i];
                char cs = stored[i];
                bool fIsSep = cf == '\\' || cf == '/';
                bool sIsSep = cs == '\\' || cs == '/';
                if (fIsSep)
                {
                    if (!sIsSep)
                    {
                        return false;
                    }
                }
                else
                {
                    if (char.ToUpperInvariant(cf) != char.ToUpperInvariant(cs))
                    {
                        return false;
                    }
                }
            }
        }
        else
        {
            for (int i = 0; i < fromLen; i++)
            {
                if (from[i] != stored[i])
                {
                    return false;
                }
            }
        }

        if (stored.Length == fromLen)
        {
            matchLength = fromLen;
            return true;
        }

        char lastFromChar = from[^1];
        if (lastFromChar == '\\' || lastFromChar == '/')
        {
            matchLength = fromLen;
            return true;
        }

        char nextStoredChar = stored[fromLen];
        if (nextStoredChar == '\\' || nextStoredChar == '/')
        {
            matchLength = fromLen;
            return true;
        }

        return false;
    }

    private static string NormalizePosixPath(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return path;
        }

        bool isAbsolute = path.StartsWith('/');
        string[] segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        List<string> resultSegments = new(segments.Length);

        foreach (string segment in segments)
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (resultSegments.Count > 0 && resultSegments[^1] != "..")
                {
                    resultSegments.RemoveAt(resultSegments.Count - 1);
                }
                else if (!isAbsolute)
                {
                    resultSegments.Add("..");
                }
            }
            else
            {
                resultSegments.Add(segment);
            }
        }

        if (isAbsolute)
        {
            return "/" + string.Join('/', resultSegments);
        }

        return resultSegments.Count == 0 ? "." : string.Join('/', resultSegments);
    }

    private static string CombinePosix(string dir, string relative)
    {
        if (string.IsNullOrEmpty(dir))
        {
            return relative;
        }

        if (string.IsNullOrEmpty(relative))
        {
            return dir;
        }

        if (relative.StartsWith('/'))
        {
            return relative;
        }

        string trimmedDir = dir.EndsWith('/') ? dir : dir + "/";
        return trimmedDir + relative;
    }

    private static string ResolveWindowsEmulated(string stored, string projectDirectory)
    {
        if (Classify(stored) == StoredPathKind.WindowsAbsolute)
        {
            return stored;
        }

        string drive = (projectDirectory.Length >= 2 && char.IsAsciiLetter(projectDirectory[0]) && projectDirectory[1] == ':')
            ? projectDirectory.Substring(0, 2)
            : "C:";

        if (stored.StartsWith('/') || stored.StartsWith('\\'))
        {
            string norm = stored.Replace('/', '\\');
            return drive + (norm.StartsWith('\\') ? norm : "\\" + norm);
        }

        string winProj = projectDirectory.Replace('/', '\\');
        string winRel = stored.Replace('/', '\\');
        string combined = winProj.EndsWith('\\') ? winProj + winRel : winProj + "\\" + winRel;
        return NormalizeWindowsPath(combined);
    }

    private static string NormalizeWindowsPath(string path)
    {
        string prefix = "";
        string toNormalize = path;
        if (toNormalize.Length >= 2 && char.IsAsciiLetter(toNormalize[0]) && toNormalize[1] == ':')
        {
            prefix = string.Concat(toNormalize.AsSpan(0, 2), "\\");
            toNormalize = toNormalize.Substring(2).TrimStart('\\', '/');
        }
        else if (toNormalize.StartsWith(@"\\", StringComparison.Ordinal) || toNormalize.StartsWith("//", StringComparison.Ordinal))
        {
            int secondSep = toNormalize.IndexOfAny(['\\', '/'], 2);
            if (secondSep >= 0)
            {
                int thirdSep = toNormalize.IndexOfAny(['\\', '/'], secondSep + 1);
                if (thirdSep >= 0)
                {
                    prefix = toNormalize.Substring(0, thirdSep).Replace('/', '\\') + "\\";
                    toNormalize = toNormalize.Substring(thirdSep).TrimStart('\\', '/');
                }
                else
                {
                    return toNormalize.Replace('/', '\\');
                }
            }
        }

        string[] segments = toNormalize.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        List<string> result = new(segments.Length);
        foreach (string seg in segments)
        {
            if (seg == ".")
            {
                continue;
            }

            if (seg == "..")
            {
                if (result.Count > 0)
                {
                    result.RemoveAt(result.Count - 1);
                }
            }
            else
            {
                result.Add(seg);
            }
        }

        return prefix + string.Join('\\', result);
    }

    private static string GetEntryName(string entry)
    {
        int lastSep = entry.LastIndexOfAny(['/', '\\']);
        return lastSep >= 0 ? entry.Substring(lastSep + 1) : entry;
    }
}
