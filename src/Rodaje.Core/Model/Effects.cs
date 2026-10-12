using System.Text.Json.Serialization;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Model;

/// <summary>Efecto aplicado a un clip o a una pista: un tipo (por ejemplo <c>rodaje.transform</c>) y sus parámetros.</summary>
public sealed class EffectInstance
{
    public string Id { get; set; } = "";

    public string Type { get; set; } = "";

    public bool Enabled { get; set; } = true;

    public Dictionary<string, Parameter> Parameters { get; set; } = [];

    /// <summary>Opciones no numéricas: modo de mezcla, LUT, preajuste de origen. Ver <see cref="Effects.EffectRegistry"/>.</summary>
    public Dictionary<string, string> Options { get; set; } = [];

    public EffectInstance Clone()
    {
        EffectInstance copy = (EffectInstance)MemberwiseClone();
        copy.Parameters = Parameters.ToDictionary(p => p.Key, p => p.Value.Clone());
        copy.Options = new Dictionary<string, string>(Options);
        return copy;
    }
}

/// <summary>Cómo se pasa de un keyframe al siguiente. Las fórmulas están en <see cref="KeyframeMath.Ease"/>.</summary>
public enum Interpolation
{
    /// <summary>Mantiene el valor hasta el siguiente keyframe y salta.</summary>
    Hold,
    Linear,
    EaseIn,
    EaseOut,
    EaseInOut,

    /// <summary>Curva Bézier de tiempo con los puntos de <see cref="Keyframe.Ease"/> (sin ellos, como <see cref="EaseInOut"/>).</summary>
    Bezier,

    /// <summary>Llega rebotando (tres botes que se apagan).</summary>
    Bounce,

    /// <summary>Se pasa del destino y vuelve oscilando, como un resorte.</summary>
    Elastic,
}

/// <summary>Un valor fijado en un instante (tiempo del origen del clip).</summary>
/// <remarks>
/// <see cref="Ease"/> da la forma de la curva de tiempo cuando la interpolación es <see cref="Interpolation.Bezier"/>.
/// <see cref="SpatialIn"/> y <see cref="SpatialOut"/> son las tangentes de la trayectoria (en unidades del valor,
/// relativas al keyframe): en la posición, las de X y las de Y forman los manejadores de la curva que sigue la capa.
/// </remarks>
public sealed record Keyframe(Time Time, double Value, Interpolation Interpolation = Interpolation.Linear)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BezierEase? Ease { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? SpatialIn { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? SpatialOut { get; init; }

    /// <summary>Lleva tangentes de trayectoria.</summary>
    [JsonIgnore]
    public bool IsSpatial => SpatialIn is not null || SpatialOut is not null;
}

/// <summary>Parámetro escalar animable; sin keyframes vale <see cref="Value"/>.</summary>
public sealed class Parameter
{
    public double Value { get; set; }

    /// <summary>Keyframes ordenados por tiempo. Vacío si el parámetro es constante.</summary>
    public List<Keyframe> Keyframes { get; set; } = [];

    /// <summary>Valor en el instante dado; entre dos keyframes manda la interpolación del de la izquierda.</summary>
    public double Evaluate(Time sourceTime)
    {
        if (Keyframes.Count == 0)
        {
            return Value;
        }

        if (sourceTime <= Keyframes[0].Time)
        {
            return Keyframes[0].Value;
        }

        if (sourceTime >= Keyframes[^1].Time)
        {
            return Keyframes[^1].Value;
        }

        int right = Keyframes.FindIndex(k => k.Time > sourceTime);
        Keyframe a = Keyframes[right - 1];
        Keyframe b = Keyframes[right];
        double t = (double)(sourceTime - a.Time).Ticks / (b.Time - a.Time).Ticks;
        return KeyframeMath.Segment(a, b, KeyframeMath.Ease(a.Interpolation, a.Ease, t));
    }

    public Parameter Clone() => new() { Value = Value, Keyframes = [.. Keyframes] };
}
