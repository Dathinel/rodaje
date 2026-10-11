using System.Text.Json.Serialization;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Model;

/// <summary>
/// Transición en el corte entre dos clips contiguos de una pista, centrada en él: dura <see cref="Duration"/>, la mitad
/// antes del corte y la mitad después. Los clips no se solapan; cada lado usa el material que sobra más allá de su
/// borde y, si no hay, congela el cuadro del borde.
/// </summary>
public sealed class TransitionInstance
{
    public string Id { get; set; } = "";

    /// <summary>Tipo registrado en <see cref="Effects.EffectRegistry"/> con destino de transición, por ejemplo <c>rodaje.crossfade</c>.</summary>
    public string Type { get; set; } = "";

    /// <summary>Clip que sale: termina en el corte.</summary>
    public string From { get; set; } = "";

    /// <summary>Clip que entra: empieza en el corte.</summary>
    public string To { get; set; } = "";

    public Time Duration { get; set; }

    /// <summary>Parámetros numéricos constantes (sin keyframes).</summary>
    public Dictionary<string, double> Parameters { get; set; } = [];

    /// <summary>Opciones no numéricas, por ejemplo la referencia del preajuste de un paquete.</summary>
    public Dictionary<string, string> Options { get; set; } = [];

    /// <summary>Mitad de la duración antes del corte; la otra mitad, la que sobra en ticks impares, va después.</summary>
    [JsonIgnore]
    public Time Before => Time.FromTicks(Duration.Ticks / 2);

    [JsonIgnore]
    public Time After => Duration - Before;

    public TransitionInstance Clone()
    {
        TransitionInstance copy = (TransitionInstance)MemberwiseClone();
        copy.Parameters = new Dictionary<string, double>(Parameters);
        copy.Options = new Dictionary<string, string>(Options);
        return copy;
    }
}
