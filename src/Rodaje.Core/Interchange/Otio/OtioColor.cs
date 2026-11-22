namespace Rodaje.Core.Interchange.Otio;

/// <summary>Conversión entre colores de marcadores de OTIO y colores hex de Rodaje.</summary>
internal static class OtioColor
{
    private static readonly (string Name, int R, int G, int B)[] StandardColors =
    [
        ("RED", 255, 0, 0),
        ("GREEN", 0, 255, 0),
        ("BLUE", 0, 0, 255),
        ("YELLOW", 255, 255, 0),
        ("CYAN", 0, 255, 255),
        ("MAGENTA", 255, 0, 255),
        ("ORANGE", 255, 165, 0),
        ("PURPLE", 128, 0, 128),
        ("PINK", 255, 192, 203),
        ("BLACK", 0, 0, 0),
        ("WHITE", 255, 255, 255),
    ];

    public static string ToOtioColor(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            return "GREEN";
        }

        string trimmed = hex.Trim();
        if (trimmed.StartsWith('#') && trimmed.Length == 7 &&
            int.TryParse(trimmed.AsSpan(1, 2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out int r) &&
            int.TryParse(trimmed.AsSpan(3, 2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out int g) &&
            int.TryParse(trimmed.AsSpan(5, 2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out int b))
        {
            int bestDistance = int.MaxValue;
            string bestName = "GREEN";
            foreach ((string name, int cr, int cg, int cb) in StandardColors)
            {
                int dr = r - cr;
                int dg = g - cg;
                int db = b - cb;
                int dist = dr * dr + dg * dg + db * db;
                if (dist < bestDistance)
                {
                    bestDistance = dist;
                    bestName = name;
                }
            }

            return bestName;
        }

        return "GREEN";
    }

    public static string FromOtioColor(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "#00FF00";
        }

        string upper = name.Trim().ToUpperInvariant();
        foreach ((string colorName, int r, int g, int b) in StandardColors)
        {
            if (colorName == upper)
            {
                return FormattableString.Invariant($"#{r:X2}{g:X2}{b:X2}");
            }
        }

        return "#00FF00";
    }
}
