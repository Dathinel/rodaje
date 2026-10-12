namespace Rodaje.Packages.Content;

public enum TextStyleAlign
{
    Left,
    Center,
    Right,
}

public enum TextStyleJoin
{
    Round,
    Miter,
    Bevel,
}

/// <summary>Fuente de un estilo: familia, peso de 100 a 900, cursiva, el elemento <c>font</c> de origen y respaldos.</summary>
public sealed record TextStyleFont(string Family, int Weight, bool Italic, string? Source, IReadOnlyList<string> Fallback);

public sealed record TextStyleStroke(string Color, double Width, TextStyleJoin Join);

public sealed record TextStyleShadow(string Color, double Dx, double Dy, double Blur);

/// <summary>Fondo detrás del texto, con margen horizontal y vertical.</summary>
public sealed record TextStyleBackground(string Color, double PaddingX, double PaddingY, double Radius);

/// <summary>
/// Entrada o salida: curvas de <c>rodaje.transform</c> (más <c>blur</c>) en segundos desde su borde, por bloque o (v2) por
/// <c>line</c>, <c>word</c> o <c>letter</c> con desfase y orden (<c>forward</c>, <c>backward</c>, <c>center</c>,
/// <c>edges</c>, <c>random</c>).
/// </summary>
public sealed record TextStyleAnimation(double DurationSeconds, IReadOnlyList<PresetCurve> Curves)
{
    public string Unit { get; init; } = "block";

    public double Stagger { get; init; }

    public string Order { get; init; } = "forward";
}

/// <summary>Degradado del relleno (v2): <c>linear</c> con ángulo en grados o <c>radial</c>; de 2 a 8 paradas en orden.</summary>
public sealed record TextStyleGradient(string Kind, double Angle, IReadOnlyList<TextStyleGradientStop> Stops);

public sealed record TextStyleGradientStop(double Position, string Color);

/// <summary>Tipografía (v2): kerning, mayúsculas (<c>none</c>, <c>upper</c>, <c>lower</c>, <c>smallCaps</c>), funciones OpenType y ajuste (<c>none</c>, <c>shrink</c>, <c>fill</c>).</summary>
public sealed record TextStyleTypography(bool Kerning, string Case, IReadOnlyList<string> Features, string Fit);

/// <summary>
/// <c>rodaje.textstyle</c> v1: un estilo de título. Medidas en píxeles de <see cref="ReferenceWidth"/> ×
/// <see cref="ReferenceHeight"/>; colores <c>#RRGGBB[AA]</c>. <see cref="HighlightColor"/> es el color de la palabra que se
/// está diciendo en los subtítulos con tiempos por palabra (karaoke). Al aplicarlo, el proyecto guarda una copia (D-029).
/// </summary>
public sealed record TextStylePreset(
    string Id,
    string Name,
    TextStyleFont Font,
    double Size,
    int ReferenceWidth,
    int ReferenceHeight,
    double LineHeight,
    double LetterSpacing,
    TextStyleAlign Align,
    string FillColor,
    TextStyleStroke? Stroke,
    TextStyleShadow? Shadow,
    TextStyleBackground? Background,
    TextStyleAnimation? In,
    TextStyleAnimation? Out,
    string? HighlightColor = null)
{
    public const string Format = "rodaje.textstyle";

    /// <summary>La 2 suma degradado, bordes y sombras adicionales, tipografía, arco, barrido de karaoke y animación por unidades; la 1 se sigue leyendo.</summary>
    public const int Version = 2;

    public TextStyleGradient? Gradient { get; init; }

    public IReadOnlyList<TextStyleStroke> ExtraStrokes { get; init; } = [];

    public IReadOnlyList<TextStyleShadow> ExtraShadows { get; init; } = [];

    public TextStyleTypography? Typography { get; init; }

    /// <summary>Grados del arco del texto; nulo en línea recta.</summary>
    public double? PathBend { get; init; }

    /// <summary><c>word</c> (la palabra que suena) o <c>sweep</c> (barrido).</summary>
    public string HighlightMode { get; init; } = "word";
}

