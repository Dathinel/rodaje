namespace Rodaje.Paquetes.Content;

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

/// <summary>Entrada o salida del bloque: curvas de <c>rodaje.transform</c> en segundos desde su borde.</summary>
public sealed record TextStyleAnimation(double DurationSeconds, IReadOnlyList<PresetCurve> Curves);

/// <summary>
/// <c>rodaje.textstyle</c> v1: un estilo de título. Medidas en píxeles de <see cref="ReferenceWidth"/> ×
/// <see cref="ReferenceHeight"/>; colores <c>#RRGGBB[AA]</c>. Al aplicarlo, el proyecto guarda una copia (D-029).
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
    TextStyleAnimation? Out)
{
    public const string Format = "rodaje.textstyle";
    public const int Version = 1;
}
