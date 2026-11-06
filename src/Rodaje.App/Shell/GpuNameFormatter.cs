using System.Text.RegularExpressions;

namespace Rodaje.App.Shell;

/// <summary>
/// Formateo y abreviación de nombres de GPU para la barra de título.
/// </summary>
internal static partial class GpuNameFormatter
{
    [GeneratedRegex(@"\s+")]
    private static partial Regex MultipleSpaces();

    /// <summary>
    /// Abreviatura legible de un nombre o descripción de GPU.
    /// Por ejemplo: «NVIDIA GeForce RTX 3050 Laptop GPU» → «RTX 3050 Laptop».
    /// </summary>
    public static string Abbreviate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "";
        }

        string s = raw.Trim();

        // Extraer solo el nombre del dispositivo si viene con información adicional como «(nivel 12_1, D3D11VA)»
        int parenIndex = s.IndexOf(" (nivel ", StringComparison.OrdinalIgnoreCase);
        if (parenIndex >= 0)
        {
            s = s[..parenIndex].Trim();
        }

        // Retirar marcas comerciales
        s = s.Replace("(R)", "", StringComparison.OrdinalIgnoreCase)
             .Replace("(TM)", "", StringComparison.OrdinalIgnoreCase)
             .Replace("®", "", StringComparison.OrdinalIgnoreCase)
             .Replace("™", "", StringComparison.OrdinalIgnoreCase);

        s = MultipleSpaces().Replace(s, " ").Trim();

        // Dispositivos por software conocidos
        if (s.Equals("Microsoft Basic Render Driver", StringComparison.OrdinalIgnoreCase))
        {
            return "WARP";
        }

        // Prefijos comunes de fabricante
        if (s.StartsWith("NVIDIA GeForce ", StringComparison.OrdinalIgnoreCase))
        {
            s = s["NVIDIA GeForce ".Length..];
        }
        else if (s.StartsWith("NVIDIA ", StringComparison.OrdinalIgnoreCase))
        {
            s = s["NVIDIA ".Length..];
        }
        else if (s.StartsWith("AMD Radeon RX ", StringComparison.OrdinalIgnoreCase))
        {
            s = "RX " + s["AMD Radeon RX ".Length..];
        }
        else if (s.StartsWith("AMD Radeon ", StringComparison.OrdinalIgnoreCase))
        {
            s = "Radeon " + s["AMD Radeon ".Length..];
        }
        else if (s.StartsWith("AMD ", StringComparison.OrdinalIgnoreCase))
        {
            s = s["AMD ".Length..];
        }
        else if (s.StartsWith("Intel ", StringComparison.OrdinalIgnoreCase))
        {
            s = s["Intel ".Length..];
        }

        s = s.Trim();

        // Sufijos redundantes
        if (s.EndsWith(" GPU", StringComparison.OrdinalIgnoreCase))
        {
            s = s[..^" GPU".Length].Trim();
        }

        if (s.EndsWith(" Graphics", StringComparison.OrdinalIgnoreCase) &&
            (s.StartsWith("Arc ", StringComparison.OrdinalIgnoreCase) ||
             s.StartsWith("Iris ", StringComparison.OrdinalIgnoreCase)))
        {
            s = s[..^" Graphics".Length].Trim();
        }

        return s;
    }
}
