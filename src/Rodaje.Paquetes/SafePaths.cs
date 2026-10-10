namespace Rodaje.Paquetes;

/// <summary>
/// Rutas que vienen de un paquete (entradas del zip y archivos del manifiesto): solo se aceptan relativas que no puedan
/// salir de su carpeta ni confundir a Windows.
/// </summary>
public static class SafePaths
{
    public const int MaxRelativeLength = 240;

    private static readonly string[] ReservedNames =
    [
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
    ];

    /// <summary>
    /// Normaliza una ruta relativa a segmentos separados por <c>/</c>. Rechaza rutas con raíz o unidad, <c>..</c> y
    /// <c>.</c>, segmentos vacíos, dos puntos (unidades y flujos alternos de NTFS), caracteres de control o prohibidos,
    /// marcas de dirección (bidi), nombres reservados de Windows y segmentos que terminan en punto o espacio (Windows los recorta y dos entradas
    /// distintas acabarían en el mismo archivo).
    /// </summary>
    public static bool TryNormalizeRelative(string? relative, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrEmpty(relative) || relative.Length > MaxRelativeLength)
        {
            return false;
        }

        string[] segments = relative.Split('/', '\\');
        foreach (string segment in segments)
        {
            if (!IsSafeSegment(segment))
            {
                return false;
            }
        }

        normalized = string.Join('/', segments);
        return true;
    }

    /// <summary>Si <paramref name="fullPath"/> queda dentro de <paramref name="directory"/> (sin ser ella misma).</summary>
    public static bool IsInside(string fullPath, string directory)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(fullPath).StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSafeSegment(string segment)
    {
        if (segment.Length == 0 || segment is "." or ".." || segment[^1] is '.' or ' ' || segment[0] == ' ')
        {
            return false;
        }

        foreach (char c in segment)
        {
            if (c < 0x20 || c is '<' or '>' or ':' or '"' or '|' or '?' or '*' or '\u007f' || SafeText.IsBidiControl(c))
            {
                return false;
            }
        }

        return !IsReservedName(segment);
    }

    /// <summary>Si Windows reserva el nombre (<c>CON</c>, <c>NUL</c>, <c>COM1</c>…), también con extensión: <c>nul.txt</c>.</summary>
    public static bool IsReservedName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        int dot = name.IndexOf('.', StringComparison.Ordinal);
        string stem = (dot < 0 ? name : name[..dot]).TrimEnd(' ');
        return ReservedNames.Contains(stem, StringComparer.OrdinalIgnoreCase);
    }
}
