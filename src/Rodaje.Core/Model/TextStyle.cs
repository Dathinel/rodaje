using System.Text.Json.Serialization;

namespace Rodaje.Core.Model;

public enum TextAlign
{
    Left,
    Center,
    Right,
}

public enum StrokeJoin
{
    Round,
    Miter,
    Bevel,
}

/// <summary>
/// Copia en el clip de un estilo <c>rodaje.textstyle</c> v1 (D-029). Medidas en píxeles de <see cref="Reference"/>, que
/// se escalan a la secuencia; colores <c>#RRGGBB[AA]</c> con gamma. Editarla no toca el paquete de <see cref="Source"/>.
/// </summary>
public sealed class TextStyle
{
    /// <summary>Referencia <c>paquete@versión/estilo</c> de donde salió la copia; solo informa (para «reaplicar estilo»).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Source { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }

    public StyleFont Font { get; set; } = new();

    public double Size { get; set; } = 96;

    public StyleReference Reference { get; set; } = new();

    public double LineHeight { get; set; } = 1.2;

    public double LetterSpacing { get; set; }

    public TextAlign Align { get; set; } = TextAlign.Center;

    public StyleFill Fill { get; set; } = new();

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StyleStroke? Stroke { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StyleShadow? Shadow { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StyleBackground? Background { get; set; }

    /// <summary>Entrada, anclada al borde izquierdo del clip (si <see cref="GeneratorEdges.In"/>).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StyleAnimation? In { get; set; }

    /// <summary>Salida, anclada al borde derecho del clip (si <see cref="GeneratorEdges.Out"/>).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StyleAnimation? Out { get; set; }

    public TextStyle Clone() => new()
    {
        Source = Source,
        Name = Name,
        Font = Font.Clone(),
        Size = Size,
        Reference = new StyleReference { Width = Reference.Width, Height = Reference.Height },
        LineHeight = LineHeight,
        LetterSpacing = LetterSpacing,
        Align = Align,
        Fill = new StyleFill { Color = Fill.Color },
        Stroke = Stroke is null ? null : new StyleStroke { Color = Stroke.Color, Width = Stroke.Width, Join = Stroke.Join },
        Shadow = Shadow is null ? null : new StyleShadow { Color = Shadow.Color, Dx = Shadow.Dx, Dy = Shadow.Dy, Blur = Shadow.Blur },
        Background = Background?.Clone(),
        In = In?.Clone(),
        Out = Out?.Clone(),
    };

    public bool ContentEquals(TextStyle other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Source == other.Source
            && Name == other.Name
            && Font.ContentEquals(other.Font)
            && Size.Equals(other.Size)
            && Reference.Width == other.Reference.Width
            && Reference.Height == other.Reference.Height
            && LineHeight.Equals(other.LineHeight)
            && LetterSpacing.Equals(other.LetterSpacing)
            && Align == other.Align
            && Fill.Color == other.Fill.Color
            && Same(Stroke, other.Stroke, (a, b) => a.Color == b.Color && a.Width.Equals(b.Width) && a.Join == b.Join)
            && Same(Shadow, other.Shadow, (a, b) => a.Color == b.Color && a.Dx.Equals(b.Dx) && a.Dy.Equals(b.Dy) && a.Blur.Equals(b.Blur))
            && Same(Background, other.Background, (a, b) => a.Color == b.Color && a.Radius.Equals(b.Radius) && a.Padding.SequenceEqual(b.Padding))
            && Same(In, other.In, (a, b) => a.ContentEquals(b))
            && Same(Out, other.Out, (a, b) => a.ContentEquals(b));
    }

    private static bool Same<T>(T? a, T? b, Func<T, T, bool> equals)
        where T : class => a is null ? b is null : b is not null && equals(a, b);
}

public sealed class StyleFont
{
    public string Family { get; set; } = "Segoe UI";

    /// <summary>Peso de 100 a 900.</summary>
    public int Weight { get; set; } = 400;

    public bool Italic { get; set; }

    /// <summary>Referencia <c>paquete@versión/elemento</c> del elemento <c>font</c>; nula si es del sistema.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Source { get; set; }

    /// <summary>Familias de respaldo, en orden, si la fuente falta en el equipo (hasta 8).</summary>
    public List<string> Fallback { get; set; } = [];

    public StyleFont Clone() => new() { Family = Family, Weight = Weight, Italic = Italic, Source = Source, Fallback = [.. Fallback] };

    public bool ContentEquals(StyleFont other) =>
        Family == other.Family && Weight == other.Weight && Italic == other.Italic && Source == other.Source
        && Fallback.SequenceEqual(other.Fallback, StringComparer.Ordinal);
}

/// <summary>Tamaño de cuadro para el que están las medidas del estilo.</summary>
public sealed class StyleReference
{
    public int Width { get; set; } = 1920;

    public int Height { get; set; } = 1080;
}

public sealed class StyleFill
{
    public string Color { get; set; } = "#FFFFFF";
}

public sealed class StyleStroke
{
    public string Color { get; set; } = "#000000";

    /// <summary>Ancho del borde, como mucho la mitad del tamaño.</summary>
    public double Width { get; set; }

    public StrokeJoin Join { get; set; }
}

public sealed class StyleShadow
{
    public string Color { get; set; } = "#00000099";

    public double Dx { get; set; }

    public double Dy { get; set; }

    /// <summary>Radio de desenfoque, de 0 a 64.</summary>
    public double Blur { get; set; }
}

public sealed class StyleBackground
{
    public string Color { get; set; } = "#00000080";

    /// <summary>Margen horizontal y vertical: exactamente dos valores.</summary>
    public List<double> Padding { get; set; } = [0, 0];

    public double Radius { get; set; }

    public StyleBackground Clone() => new() { Color = Color, Padding = [.. Padding], Radius = Radius };
}

/// <summary>
/// Animación de entrada o salida del bloque: curvas sobre <c>x</c>, <c>y</c>, <c>scale</c>, <c>rotation</c> y
/// <c>opacity</c> (los nombres de <see cref="BuiltInEffects.TransformParameters"/>), en segundos desde su borde. Es un
/// delta sobre la transformación del clip: suma en posición y rotación, multiplica en escala y opacidad.
/// </summary>
public sealed class StyleAnimation
{
    public double DurationSeconds { get; set; }

    public Dictionary<string, List<StyleKey>> Curves { get; set; } = [];

    public StyleAnimation Clone() => new()
    {
        DurationSeconds = DurationSeconds,
        Curves = Curves.ToDictionary(c => c.Key, c => (List<StyleKey>)[.. c.Value], StringComparer.Ordinal),
    };

    public bool ContentEquals(StyleAnimation other)
    {
        if (!DurationSeconds.Equals(other.DurationSeconds) || Curves.Count != other.Curves.Count)
        {
            return false;
        }

        foreach ((string name, List<StyleKey> keys) in Curves)
        {
            if (!other.Curves.TryGetValue(name, out List<StyleKey>? others) || !keys.SequenceEqual(others))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>Punto de una curva del estilo: <see cref="T"/> en segundos desde el borde y la interpolación hasta el siguiente.</summary>
public sealed record StyleKey(double T, double Value, Interpolation Interp = Interpolation.Linear);
