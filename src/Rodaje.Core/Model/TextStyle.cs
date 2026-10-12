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

/// <summary>Mayúsculas del texto al dibujarlo; el texto guardado no cambia.</summary>
public enum TextCase
{
    None,
    Upper,
    Lower,

    /// <summary>Versalitas sintéticas: las minúsculas pasan a mayúsculas al 75 % (con <c>smcp</c> en las funciones, las de la fuente).</summary>
    SmallCaps,
}

/// <summary>Ajuste del tamaño a la caja del texto (<see cref="TextFrame"/>).</summary>
public enum TextFit
{
    None,

    /// <summary>Solo achica, si no cabe.</summary>
    Shrink,

    /// <summary>Achica o agranda hasta llenar la caja.</summary>
    Fill,
}

public enum GradientKind
{
    Linear,
    Radial,
}

/// <summary>Qué se anima por separado: el bloque, cada línea, cada palabra o cada letra.</summary>
public enum TextUnit
{
    Block,
    Line,
    Word,
    Letter,
}

/// <summary>En qué orden arrancan las unidades con desfase.</summary>
public enum UnitOrder
{
    Forward,
    Backward,

    /// <summary>Del centro hacia los extremos.</summary>
    Center,

    /// <summary>De los extremos hacia el centro.</summary>
    Edges,

    /// <summary>Un orden mezclado fijo: el mismo en cada cuadro y en la exportación.</summary>
    Random,
}

/// <summary>Karaoke: solo la palabra que suena, o un barrido de color que avanza con la voz y deja pintado lo dicho.</summary>
public enum HighlightMode
{
    Word,
    Sweep,
}

public enum TextPathKind
{
    Arc,
}

/// <summary>
/// Copia en el clip de un estilo <c>rodaje.textstyle</c> (D-029). Medidas en píxeles de <see cref="Reference"/>, que
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

    /// <summary>Bordes además de <see cref="Stroke"/>, cada uno con su ancho desde el contorno de la letra; el más ancho va debajo.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<StyleStroke>? ExtraStrokes { get; set; }

    /// <summary>Sombras además de <see cref="Shadow"/>, que se dibujan encima de ella en este orden.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<StyleShadow>? ExtraShadows { get; set; }

    /// <summary>Kerning, mayúsculas, funciones OpenType y ajuste a la caja; nulo = lo de siempre.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StyleTypography? Typography { get; set; }

    /// <summary>Texto sobre una trayectoria (un arco); nulo = en línea recta.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StylePath? Path { get; set; }

    /// <summary>
    /// Karaoke: en un subtítulo con palabras transcritas, la que suena se pinta de este color (o un barrido). Solo del
    /// proyecto y de los estilos de paquete que la traen.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StyleHighlight? Highlight { get; set; }

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
        Fill = Fill.Clone(),
        Stroke = Stroke?.Clone(),
        Shadow = Shadow?.Clone(),
        Background = Background?.Clone(),
        ExtraStrokes = ExtraStrokes?.ConvertAll(s => s.Clone()),
        ExtraShadows = ExtraShadows?.ConvertAll(s => s.Clone()),
        Typography = Typography?.Clone(),
        Path = Path is null ? null : new StylePath { Kind = Path.Kind, Bend = Path.Bend },
        Highlight = Highlight is null ? null : new StyleHighlight { Color = Highlight.Color, Mode = Highlight.Mode },
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
            && Fill.ContentEquals(other.Fill)
            && Same(Stroke, other.Stroke, (a, b) => a.ContentEquals(b))
            && Same(Shadow, other.Shadow, (a, b) => a.ContentEquals(b))
            && Same(Background, other.Background, (a, b) => a.Color == b.Color && a.Radius.Equals(b.Radius) && a.Padding.SequenceEqual(b.Padding))
            && SameList(ExtraStrokes, other.ExtraStrokes, (a, b) => a.ContentEquals(b))
            && SameList(ExtraShadows, other.ExtraShadows, (a, b) => a.ContentEquals(b))
            && Same(Typography, other.Typography, (a, b) => a.ContentEquals(b))
            && Same(Path, other.Path, (a, b) => a.Kind == b.Kind && a.Bend.Equals(b.Bend))
            && Same(Highlight, other.Highlight, (a, b) => a.Color == b.Color && a.Mode == b.Mode)
            && Same(In, other.In, (a, b) => a.ContentEquals(b))
            && Same(Out, other.Out, (a, b) => a.ContentEquals(b));
    }

    /// <summary>Todos los bordes, el principal primero.</summary>
    public IEnumerable<StyleStroke> AllStrokes()
    {
        if (Stroke is { } stroke)
        {
            yield return stroke;
        }

        foreach (StyleStroke extra in ExtraStrokes ?? [])
        {
            yield return extra;
        }
    }

    /// <summary>Todas las sombras, la principal primero (es la de más abajo).</summary>
    public IEnumerable<StyleShadow> AllShadows()
    {
        if (Shadow is { } shadow)
        {
            yield return shadow;
        }

        foreach (StyleShadow extra in ExtraShadows ?? [])
        {
            yield return extra;
        }
    }

    private static bool Same<T>(T? a, T? b, Func<T, T, bool> equals)
        where T : class => a is null ? b is null : b is not null && equals(a, b);

    private static bool SameList<T>(List<T>? a, List<T>? b, Func<T, T, bool> equals)
        where T : class => (a?.Count ?? 0) == (b?.Count ?? 0) && (a is null || b is null || a.Zip(b).All(p => equals(p.First, p.Second)));
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

    /// <summary>Degradado sobre todo el bloque; si está, manda sobre <see cref="Color"/> (que queda para las muestras).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StyleGradient? Gradient { get; set; }

    public StyleFill Clone() => new() { Color = Color, Gradient = Gradient?.Clone() };

    public bool ContentEquals(StyleFill other) =>
        Color == other.Color && (Gradient is null ? other.Gradient is null : other.Gradient is not null && Gradient.ContentEquals(other.Gradient));
}

/// <summary>
/// Degradado del relleno en la caja del bloque: lineal con <see cref="Angle"/> en grados (0 = de izquierda a derecha, 90 =
/// de arriba abajo) o radial desde el centro. De 2 a 8 paradas con posición de 0 a 1, en orden.
/// </summary>
public sealed class StyleGradient
{
    public const int MaxStops = 8;

    public GradientKind Kind { get; set; }

    public double Angle { get; set; }

    public List<ColorStop> Stops { get; set; } = [];

    public StyleGradient Clone() => new() { Kind = Kind, Angle = Angle, Stops = [.. Stops] };

    public bool ContentEquals(StyleGradient other) => Kind == other.Kind && Angle.Equals(other.Angle) && Stops.SequenceEqual(other.Stops);
}

public sealed record ColorStop(double Position, string Color);

public sealed class StyleStroke
{
    public string Color { get; set; } = "#000000";

    /// <summary>Ancho del borde, como mucho la mitad del tamaño.</summary>
    public double Width { get; set; }

    public StrokeJoin Join { get; set; }

    public StyleStroke Clone() => new() { Color = Color, Width = Width, Join = Join };

    public bool ContentEquals(StyleStroke other) => Color == other.Color && Width.Equals(other.Width) && Join == other.Join;
}

public sealed class StyleHighlight
{
    public string Color { get; set; } = "#FFD400";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public HighlightMode Mode { get; set; }
}

public sealed class StyleShadow
{
    public string Color { get; set; } = "#00000099";

    public double Dx { get; set; }

    public double Dy { get; set; }

    /// <summary>Radio de desenfoque, de 0 a 64.</summary>
    public double Blur { get; set; }

    public StyleShadow Clone() => new() { Color = Color, Dx = Dx, Dy = Dy, Blur = Blur };

    public bool ContentEquals(StyleShadow other) => Color == other.Color && Dx.Equals(other.Dx) && Dy.Equals(other.Dy) && Blur.Equals(other.Blur);
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
/// Tipografía fina: kerning, mayúsculas, funciones OpenType (etiquetas de cuatro letras como <c>liga</c>, <c>tnum</c> o
/// <c>ss01</c>; con <c>-</c> delante se apagan) y ajuste del tamaño a la caja.
/// </summary>
public sealed class StyleTypography
{
    public const int MaxFeatures = 16;

    public bool Kerning { get; set; } = true;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public TextCase Case { get; set; }

    public List<string> Features { get; set; } = [];

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public TextFit Fit { get; set; }

    public StyleTypography Clone() => new() { Kerning = Kerning, Case = Case, Features = [.. Features], Fit = Fit };

    public bool ContentEquals(StyleTypography other) =>
        Kerning == other.Kerning && Case == other.Case && Fit == other.Fit && Features.SequenceEqual(other.Features, StringComparer.Ordinal);

    /// <summary>Etiqueta OpenType válida: cuatro caracteres ASCII imprimibles, opcionalmente con <c>-</c> delante.</summary>
    public static bool IsFeature(string? tag)
    {
        ReadOnlySpan<char> body = tag is { Length: 5 } && tag[0] == '-' ? tag.AsSpan(1) : tag.AsSpan();
        foreach (char c in body)
        {
            if (c is < '!' or > '~')
            {
                return false;
            }
        }

        return body.Length == 4;
    }
}

/// <summary>
/// Texto sobre un arco: <see cref="Bend"/> son los grados que abarca la línea más larga (positivo, la curva sube por el
/// centro como un puente; negativo, baja como una sonrisa). Cada letra va girada con el arco. De −360 a 360.
/// </summary>
public sealed class StylePath
{
    public TextPathKind Kind { get; set; }

    public double Bend { get; set; }
}

/// <summary>
/// Animación de entrada o salida: curvas sobre <c>x</c>, <c>y</c>, <c>scale</c>, <c>rotation</c> y <c>opacity</c> (los
/// nombres de <see cref="BuiltInEffects.TransformParameters"/>) más <see cref="Blur"/>, en segundos desde su borde. Por
/// bloque es un delta sobre la transformación del clip (suma en posición y rotación, multiplica en escala y opacidad); por
/// línea, palabra o letra cada unidad hace las mismas curvas alrededor de su centro, con <see cref="Stagger"/> de desfase.
/// </summary>
public sealed class StyleAnimation
{
    /// <summary>Curva de desenfoque (radio en píxeles de la referencia); hace que la animación vaya por unidades.</summary>
    public const string Blur = "blur";

    public double DurationSeconds { get; set; }

    public Dictionary<string, List<StyleKey>> Curves { get; set; } = [];

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public TextUnit Unit { get; set; }

    /// <summary>
    /// Desfase entre unidades, de 0 a 1: la parte de <see cref="DurationSeconds"/> en que van arrancando una tras otra. Con
    /// 0 todas a la vez; con 1 cada una salta de golpe en su turno (máquina de escribir).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double Stagger { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public UnitOrder Order { get; set; }

    /// <summary>Se dibuja por unidades (no como delta de la transformación del bloque): por línea, palabra o letra, o con desenfoque.</summary>
    [JsonIgnore]
    public bool PerUnit => Unit != TextUnit.Block || Curves.ContainsKey(Blur);

    public StyleAnimation Clone() => new()
    {
        DurationSeconds = DurationSeconds,
        Curves = Curves.ToDictionary(c => c.Key, c => (List<StyleKey>)[.. c.Value], StringComparer.Ordinal),
        Unit = Unit,
        Stagger = Stagger,
        Order = Order,
    };

    public bool ContentEquals(StyleAnimation other)
    {
        if (!DurationSeconds.Equals(other.DurationSeconds) || Curves.Count != other.Curves.Count || Unit != other.Unit
            || !Stagger.Equals(other.Stagger) || Order != other.Order)
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
