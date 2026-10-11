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

/// <summary>Cómo se pasa de un keyframe al siguiente.</summary>
public enum Interpolation
{
    /// <summary>Mantiene el valor hasta el siguiente keyframe y salta.</summary>
    Hold,
    Linear,
    EaseIn,
    EaseOut,
    EaseInOut,
}

/// <summary>Un valor fijado en un instante (tiempo del origen del clip).</summary>
public sealed record Keyframe(Time Time, double Value, Interpolation Interpolation = Interpolation.Linear);

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
        return a.Value + ((b.Value - a.Value) * Ease(a.Interpolation, t));
    }

    /// <summary>Curvas de suavizado cúbicas sobre t ∈ [0, 1].</summary>
    internal static double Ease(Interpolation interpolation, double t) => interpolation switch
    {
        Interpolation.Hold => 0,
        Interpolation.Linear => t,
        Interpolation.EaseIn => t * t * t,
        Interpolation.EaseOut => 1 - Math.Pow(1 - t, 3),
        Interpolation.EaseInOut => t < 0.5 ? 4 * t * t * t : 1 - (Math.Pow((-2 * t) + 2, 3) / 2),
        _ => throw new ArgumentOutOfRangeException(nameof(interpolation), interpolation, "Interpolación desconocida."),
    };

    public Parameter Clone() => new() { Value = Value, Keyframes = [.. Keyframes] };
}
