using System.Text.Json.Serialization;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Model;

/// <summary>Tipos de <see cref="ClipGenerator.Kind"/> que el motor conoce; uno desconocido se conserva y no se dibuja.</summary>
public static class GeneratorKinds
{
    public const string Text = "text";
}

/// <summary>
/// Contenido de un clip que no viene de un medio (formato 5): un título hoy; color sólido o formas más adelante. El clip
/// tiene un origen virtual sin límites, así que su <see cref="Clip.SourceIn"/> puede ser negativo.
/// </summary>
public sealed class ClipGenerator
{
    public string Kind { get; set; } = GeneratorKinds.Text;

    /// <summary>Texto del título; admite <c>\n</c> y tabulación, ningún otro carácter de control.</summary>
    public string Text { get; set; } = "";

    /// <summary>Copia completa del estilo (D-029): el proyecto se ve igual sin el paquete del que salió.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TextStyle? Style { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TextFrame? Box { get; set; }

    /// <summary>Qué bordes del clip animan la entrada y la salida del estilo y dónde quedan si el clip salió de dividir otro.</summary>
    public GeneratorEdges Edges { get; set; } = new();

    /// <summary>
    /// Palabras transcritas con sus tiempos en el origen del clip (subtítulos automáticos), para editar por palabra;
    /// <c>null</c> en un título escrito a mano. Dividir el clip las copia enteras a los dos trozos.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<GeneratorWord>? Words { get; set; }

    /// <summary>
    /// Tramos del texto con otro formato (negrita, color, tamaño de una palabra), sin solaparse y en orden; lo que no dicen
    /// lo pone el estilo. Se mueven con el texto al editarlo (<c>TextSpans.Remap</c>).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<TextSpan>? Spans { get; set; }

    public ClipGenerator Clone() => new()
    {
        Kind = Kind,
        Text = Text,
        Style = Style?.Clone(),
        Box = Box?.Clone(),
        Edges = Edges.Clone(),
        Words = Words?.ConvertAll(w => w.Clone()),
        Spans = Spans?.ConvertAll(s => s with { }),
    };

    public bool ContentEquals(ClipGenerator other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return SameContent(other) && Edges.ContentEquals(other.Edges);
    }

    /// <summary>Igual salvo por <see cref="Edges"/>: las dos mitades de un título dividido.</summary>
    public bool SameContent(ClipGenerator other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Kind == other.Kind
            && Text == other.Text
            && (Style is null ? other.Style is null : other.Style is not null && Style.ContentEquals(other.Style))
            && (Box is null ? other.Box is null : other.Box is not null && Box.Width == other.Box.Width && Box.WidthFraction == other.Box.WidthFraction
                && Box.HeightFraction == other.Box.HeightFraction)
            && (Spans?.Count ?? 0) == (other.Spans?.Count ?? 0) && (Spans is null || other.Spans is null || Spans.SequenceEqual(other.Spans))
            && (Words is null ? other.Words is null : other.Words is not null && Words.Count == other.Words.Count
                && Words.Zip(other.Words).All(p => p.First.ContentEquals(p.Second)));
    }
}

/// <summary>
/// Bordes con animación: <see cref="In"/> el izquierdo (entrada), <see cref="Out"/> el derecho (salida). Un trozo de un
/// título dividido sigue la animación del título entero: <see cref="Before"/> y <see cref="After"/> dicen cuánto se
/// extendía el original antes de su borde izquierdo y después del derecho.
/// </summary>
public sealed class GeneratorEdges
{
    public bool In { get; set; } = true;

    public bool Out { get; set; } = true;

    /// <summary>Lo que había del título original antes del borde izquierdo: la entrada empieza ahí.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Time Before { get; set; }

    /// <summary>Lo que había del título original después del borde derecho: la salida termina ahí.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Time After { get; set; }

    public GeneratorEdges Clone() => new() { In = In, Out = Out, Before = Before, After = After };

    public bool ContentEquals(GeneratorEdges other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return In == other.In && Out == other.Out && Before == other.Before && After == other.After;
    }
}

/// <summary>Caja del texto para el ajuste de línea; sin ancho, el texto solo corta en <c>\n</c>.</summary>
public sealed class TextFrame
{
    /// <summary>Ancho en píxeles de la <see cref="TextStyle.Reference"/> del estilo.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Width { get; set; }

    /// <summary>Ancho como fracción (0, 1] del ancho de la secuencia; si está, manda sobre <see cref="Width"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? WidthFraction { get; set; }

    /// <summary>Alto de la caja como fracción (0, 1] del alto de la secuencia, para el ajuste del tamaño (<see cref="TextFit"/>).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? HeightFraction { get; set; }

    public TextFrame Clone() => new() { Width = Width, WidthFraction = WidthFraction, HeightFraction = HeightFraction };
}

/// <summary>
/// Un tramo del texto (<see cref="Start"/> y <see cref="Length"/> en caracteres UTF-16) con formato propio; lo que queda
/// nulo es lo del estilo. <see cref="Scale"/> multiplica el tamaño del estilo (de 0,1 a 10).
/// </summary>
public sealed record TextSpan
{
    public int Start { get; init; }

    public int Length { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Weight { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Italic { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Underline { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Color { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Scale { get; init; }

    [JsonIgnore]
    public int End => Start + Length;

    /// <summary>El mismo formato (sin mirar dónde está).</summary>
    public bool SameFormat(TextSpan other) =>
        Weight == other.Weight && Italic == other.Italic && Underline == other.Underline && Color == other.Color && Scale == other.Scale;

    /// <summary>No cambia nada respecto del estilo.</summary>
    [JsonIgnore]
    public bool IsPlain => Weight is null && Italic is null && Underline is null && Color is null && Scale is null;
}


/// <summary>Roles de pista conocidos (<see cref="Track.Role"/>); el nombre de la pista es solo para mostrar.</summary>
public static class TrackRoles
{
    /// <summary>Pista de video con los subtítulos (clips de texto); en la fase 7 pueden salir como pista de texto.</summary>
    public const string Subtitles = "subtitles";
}
