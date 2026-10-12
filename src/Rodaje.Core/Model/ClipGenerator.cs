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

    public ClipGenerator Clone() => new()
    {
        Kind = Kind,
        Text = Text,
        Style = Style?.Clone(),
        Box = Box?.Clone(),
        Edges = Edges.Clone(),
        Words = Words?.ConvertAll(w => w.Clone()),
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
            && (Box is null ? other.Box is null : other.Box is not null && Box.Width == other.Box.Width && Box.WidthFraction == other.Box.WidthFraction)
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

    public TextFrame Clone() => new() { Width = Width, WidthFraction = WidthFraction };
}

/// <summary>Roles de pista conocidos (<see cref="Track.Role"/>); el nombre de la pista es solo para mostrar.</summary>
public static class TrackRoles
{
    /// <summary>Pista de video con los subtítulos (clips de texto); en la fase 7 pueden salir como pista de texto.</summary>
    public const string Subtitles = "subtitles";
}
