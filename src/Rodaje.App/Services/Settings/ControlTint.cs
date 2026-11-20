namespace Rodaje.App.Services.Settings;

/// <summary>
/// Los grises de <c>ControlColors.xaml</c> (los controles de WinUI) derivados del tema por su nivel: fondo, barra, panel,
/// control, al pasar, pulsado, borde y texto. Con Grafito cada gris vuelve a ser el de fábrica, byte a byte; con un tema
/// de paneles azulados los controles toman ese azul en vez de quedarse en el gris de serie. Lo que no es un gris de
/// fábrica (el acento y sus variantes, el rojo de peligro, lo transparente) no se toca aquí.
/// </summary>
public static class ControlTint
{
    /// <summary>El blanco puro del texto al pasar o con el foco: en un tema oscuro, blanco; en uno claro, negro.</summary>
    private const string Pole = "*";

    // Un gris de fábrica y de qué clave sale; con destino, la mezcla entre dos claves (la cantidad reproduce el gris en Grafito).
    private static readonly (uint Stock, string From, string? To, double Amount)[] Levels =
    [
        (0x101010, "BackgroundBrush", null, 0),
        (0x141414, "BackgroundBrush", "PanelBrush", 4.0 / 13),
        (0x232323, "PanelBrush", "ControlBrush", 6.0 / 15),
        (0x262626, "BarBrush", null, 0),
        (0x2C2C2C, "ControlBrush", null, 0),
        (0x3A3A3A, "ControlHoverBrush", null, 0),
        (0x4A4A4A, "ControlPressedBrush", null, 0),
        (0x5C5C5C, "ControlBorderBrush", "PanelBrush", 23.0 / 86),
        (0x6E6E6E, "ControlBorderBrush", "PanelBrush", 5.0 / 86),
        (0x737373, "ControlBorderBrush", null, 0),
        (0x808080, "ControlBorderBrush", "TextBrush", 13.0 / 121),
        (0x8C8C8C, "ControlBorderBrush", "TextBrush", 25.0 / 121),
        (0xA0A0A0, "ControlBorderBrush", "TextBrush", 45.0 / 121),
        (0x7A7A7A, "TextDisabledBrush", null, 0),
        (0xB0B0B0, "TextMutedBrush", null, 0),
        (0xECECEC, "TextBrush", null, 0),
        (0xFFFFFF, "TextBrush", Pole, 1),
    ];

    /// <summary>Los grises de fábrica que se derivan, sin alfa.</summary>
    public static IReadOnlyList<uint> StockGrays { get; } = Levels.Select(l => l.Stock).ToArray();

    /// <summary>
    /// El color del tema para un gris de fábrica de los controles, con el alfa de <paramref name="original"/>; <c>null</c>
    /// si no es uno de esos grises.
    /// </summary>
    public static uint? Derive(uint original, ThemeDefinition theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        uint rgb = original & 0x00FFFFFF;
        foreach ((uint stock, string from, string? to, double amount) in Levels)
        {
            if (stock != rgb)
            {
                continue;
            }

            uint color = theme.Color(from);
            if (to is not null)
            {
                uint target = to == Pole ? TextPole(theme) : theme.Color(to);
                color = Personalization.Mix(color, target, amount);
            }

            return Personalization.WithAlpha(color, Personalization.Alpha(original));
        }

        return null;
    }

    /// <summary>Hacia dónde se intensifica el texto: blanco si el texto es más claro que los paneles, negro si no.</summary>
    public static uint TextPole(ThemeDefinition theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        return Personalization.Luminance(theme.Color("TextBrush")) > Personalization.Luminance(theme.Color("PanelBrush")) ? Personalization.White : 0xFF000000;
    }
}
