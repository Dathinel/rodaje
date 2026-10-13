namespace Rodaje.Core.Model;

/// <summary>Reglas de Windows para los nombres de archivo que propone el editor, comprobables en cualquier sistema.</summary>
public static class WindowsFileNames
{
    // CON, PRN, AUX, NUL, COM0–9, LPT0–9 (con los superíndices ¹ ² ³) y las consolas: Windows los toma por dispositivos.
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
    };

    /// <summary>
    /// Si Windows trata <paramref name="name"/> como un dispositivo: un nombre reservado, aunque lleve extensión
    /// (<c>aux.mp4</c>, <c>CON.tar.gz</c>) o espacios antes del primer punto.
    /// </summary>
    public static bool IsReserved(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        int dot = name.IndexOf('.', StringComparison.Ordinal);
        string stem = (dot < 0 ? name : name[..dot]).TrimEnd(' ');
        return Reserved.Contains(stem);
    }

    /// <summary>
    /// <paramref name="name"/> con un <c>_</c> delante si es un nombre reservado (<c>CON.mp4</c> → <c>_CON.mp4</c>);
    /// si no, tal cual.
    /// </summary>
    public static string AvoidReserved(string name) => IsReserved(name) ? "_" + name : name;
}
