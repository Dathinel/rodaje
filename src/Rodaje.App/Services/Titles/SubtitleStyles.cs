using Rodaje.App.Services.Format;
using Rodaje.Core.Model;

namespace Rodaje.App.Services.Titles;

/// <summary>
/// Estilo por defecto de las pistas de subtítulos y el aviso de legibilidad (propuesta de texto, «Accesibilidad de los
/// subtítulos»): tamaño mínimo respecto a la altura del cuadro y contraste del texto con su fondo o su borde.
/// </summary>
public static class SubtitleStyles
{
    /// <summary>Lo mínimo que mide la letra respecto al lado corto del cuadro (unos 38 px en 1080, también en vertical).</summary>
    public const double MinSizeFraction = 0.035;

    /// <summary>Contraste mínimo entre el texto y lo que lo rodea (WCAG AA para texto normal).</summary>
    public const double MinContrast = 4.5;

    /// <summary>Un fondo cuenta si es al menos así de opaco; un borde, si es al menos esta fracción del tamaño.</summary>
    private const double MinBackgroundAlpha = 0.5;
    private const double MinStrokeFraction = 0.04;

    /// <summary>Blanco sobre una caja negra semitransparente, sin animación; se lee también en una secuencia vertical.</summary>
    public static TextStyle Default() => new()
    {
        Name = "Subtítulo",
        Font = new StyleFont { Family = "Segoe UI", Weight = 600, Fallback = ["Arial"] },
        Size = 68,
        LineHeight = 1.15,
        Fill = new StyleFill { Color = "#FFFFFF" },
        Background = new StyleBackground { Color = "#000000B3", Padding = [16, 8], Radius = 6 },
    };

    /// <summary>Un subtítulo nuevo: el estilo de subtítulos y una caja del 80 % del ancho para que parta en dos líneas.</summary>
    public static ClipGenerator NewSubtitle(string text = TitleStyles.DefaultText) => new()
    {
        Kind = GeneratorKinds.Text,
        Text = text,
        Style = Default(),
        Box = new TextFrame { WidthFraction = 0.8 },
    };

    /// <summary>
    /// Por qué un estilo de subtítulo puede costar leerlo, o <c>null</c> si cumple: letra chica para la secuencia, sin
    /// fondo ni borde, o con poco contraste entre el texto y ellos.
    /// </summary>
    public static string? Legibility(TextStyle style, int sequenceWidth, int sequenceHeight)
    {
        ArgumentNullException.ThrowIfNull(style);
        double unit = Math.Min((double)sequenceWidth / style.Reference.Width, (double)sequenceHeight / style.Reference.Height);
        double size = style.Size * unit;
        double minimum = MinSizeFraction * Math.Min(sequenceWidth, sequenceHeight);
        if (size < minimum)
        {
            return string.Create(SpanishNumbers.Format,
                $"La letra mide {size:0} px y en esta secuencia un subtítulo se lee bien desde unos {minimum:0} px.");
        }

        if (!StyleColors.TryParse(style.Fill.Color, out (byte A, byte R, byte G, byte B) fill))
        {
            return null;
        }

        double best = 0;
        bool backed = false;
        if (style.Background is { } background && StyleColors.TryParse(background.Color, out var back) && back.A / 255.0 >= MinBackgroundAlpha)
        {
            backed = true;
            best = Math.Max(best, Contrast(fill, back));
        }

        if (style.Stroke is { } stroke && stroke.Width >= MinStrokeFraction * style.Size && StyleColors.TryParse(stroke.Color, out var edge) && edge.A > 0)
        {
            backed = true;
            best = Math.Max(best, Contrast(fill, edge));
        }

        if (!backed)
        {
            return "Sin fondo ni borde, el subtítulo puede perderse sobre la imagen: dale un fondo o un borde.";
        }

        return best < MinContrast
            ? string.Create(SpanishNumbers.Format, $"El texto contrasta {best:0.0}:1 con su fondo o su borde; para leerlo bien hace falta {MinContrast:0.0}:1.")
            : null;
    }

    /// <summary>Contraste WCAG entre dos colores (la transparencia no cuenta: se mira el color en sí).</summary>
    public static double Contrast((byte A, byte R, byte G, byte B) a, (byte A, byte R, byte G, byte B) b)
    {
        double la = Luminance(a);
        double lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance((byte A, byte R, byte G, byte B) c) =>
        (0.2126 * Linear(c.R)) + (0.7152 * Linear(c.G)) + (0.0722 * Linear(c.B));

    private static double Linear(byte channel)
    {
        double v = channel / 255.0;
        return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }
}
