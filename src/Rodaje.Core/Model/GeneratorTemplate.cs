using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rodaje.Core.Model;

/// <summary>
/// Plantilla de movimiento instanciada en un clip (formato 16, D-064). <see cref="Layers"/> es lo que se dibuja;
/// <see cref="Document"/> y <see cref="Values"/> solo sirven para volver a instanciar, y el núcleo no los interpreta.
/// </summary>
public sealed class GeneratorTemplate
{
    public const int MaxLayers = 16;
    public const int MaxDocumentBytes = 256 * 1024;
    public const int MaxValues = 32;
    public const double MaxSeconds = 600;

    /// <summary><c>paquete@versión/elemento</c>, o nulo si es una plantilla propia.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Source { get; set; }

    public string Name { get; set; } = "";

    /// <summary>Duración más corta a la que se puede recortar el clip, en segundos.</summary>
    public double MinSeconds { get; set; }

    /// <summary>El <c>rodaje.template</c> tal cual, como texto.</summary>
    public string Document { get; set; } = "";

    /// <summary>Valores puestos por quien edita, por id de parámetro.</summary>
    public Dictionary<string, JsonElement> Values { get; set; } = [];

    public List<TemplateLayer> Layers { get; set; } = [];

    public GeneratorTemplate Clone() => new()
    {
        Source = Source,
        Name = Name,
        MinSeconds = MinSeconds,
        Document = Document,
        Values = new Dictionary<string, JsonElement>(Values),
        Layers = Layers.ConvertAll(l => l.Clone()),
    };

    public bool ContentEquals(GeneratorTemplate other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Source == other.Source && Name == other.Name && MinSeconds.Equals(other.MinSeconds) && Document == other.Document
            && Values.Count == other.Values.Count
            && Values.All(p => other.Values.TryGetValue(p.Key, out JsonElement value) && JsonElement.DeepEquals(p.Value, value))
            && Layers.Count == other.Layers.Count && Layers.Zip(other.Layers).All(p => p.First.ContentEquals(p.Second));
    }
}

/// <summary>
/// Una capa de texto de una plantilla: un título con su posición (<see cref="X"/>, <see cref="Y"/>, en píxeles de la
/// referencia de su estilo, desde el centro) y su ventana dentro del clip (<see cref="Start"/> segundos tras el borde
/// izquierdo hasta <see cref="End"/> segundos antes del derecho).
/// </summary>
public sealed class TemplateLayer
{
    public string Id { get; set; } = "";

    public string Text { get; set; } = "";

    public TextStyle Style { get; set; } = new();

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TextFrame? Box { get; set; }

    public double X { get; set; }

    public double Y { get; set; }

    public double Start { get; set; }

    public double End { get; set; }

    public TemplateLayer Clone() => new()
    {
        Id = Id,
        Text = Text,
        Style = Style.Clone(),
        Box = Box?.Clone(),
        X = X,
        Y = Y,
        Start = Start,
        End = End,
    };

    public bool ContentEquals(TemplateLayer other) =>
        Id == other.Id && Text == other.Text && Style.ContentEquals(other.Style)
        && (Box is null ? other.Box is null : other.Box is not null && Box.Width == other.Box.Width && Box.WidthFraction == other.Box.WidthFraction
            && Box.HeightFraction == other.Box.HeightFraction)
        && X.Equals(other.X) && Y.Equals(other.Y) && Start.Equals(other.Start) && End.Equals(other.End);
}
