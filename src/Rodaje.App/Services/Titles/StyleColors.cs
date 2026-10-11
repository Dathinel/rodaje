using System.Globalization;

namespace Rodaje.App.Services.Titles;

/// <summary>Colores de los estilos de texto: <c>#RRGGBB</c> opaco o <c>#RRGGBBAA</c>, como los guarda el formato.</summary>
public static class StyleColors
{
    public static bool TryParse(string? text, out (byte A, byte R, byte G, byte B) color)
    {
        color = default;
        string value = text?.Trim() ?? "";
        if (!value.StartsWith('#'))
        {
            value = "#" + value;
        }

        if (value.Length is not (7 or 9)
            || !uint.TryParse(value.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint bits))
        {
            return false;
        }

        if (value.Length == 7)
        {
            bits = (bits << 8) | 0xFF;
        }

        color = ((byte)bits, (byte)(bits >> 24), (byte)(bits >> 16), (byte)(bits >> 8));
        return true;
    }

    /// <summary><c>#RRGGBB</c> si es opaco; si no, <c>#RRGGBBAA</c>.</summary>
    public static string Format(byte a, byte r, byte g, byte b) => a == 0xFF
        ? string.Create(CultureInfo.InvariantCulture, $"#{r:X2}{g:X2}{b:X2}")
        : string.Create(CultureInfo.InvariantCulture, $"#{r:X2}{g:X2}{b:X2}{a:X2}");

    /// <summary>El texto escrito por la persona, ya normalizado; <c>null</c> si no es un color.</summary>
    public static string? Normalize(string? text) =>
        TryParse(text, out (byte A, byte R, byte G, byte B) c) ? Format(c.A, c.R, c.G, c.B) : null;
}
