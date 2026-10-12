using Rodaje.Core.Timing;

namespace Rodaje.Core.Model;

/// <summary>
/// Una palabra transcrita de un subtítulo (<see cref="ClipGenerator.Words"/>), con sus tiempos en el origen virtual del
/// clip generador, como los keyframes: en un subtítulo recién creado el origen empieza en 0 en el borde izquierdo, así que
/// recortar o mover el clip no la desplaza. Sirve para editar por palabra; el texto que se ve es siempre
/// <see cref="ClipGenerator.Text"/>.
/// </summary>
public sealed class GeneratorWord
{
    public Time Start { get; set; }

    public Time End { get; set; }

    /// <summary>La palabra tal como se transcribió, sin espacios ni caracteres de control.</summary>
    public string Text { get; set; } = "";

    public GeneratorWord Clone() => new() { Start = Start, End = End, Text = Text };

    public bool ContentEquals(GeneratorWord other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Start == other.Start && End == other.End && Text == other.Text;
    }
}
