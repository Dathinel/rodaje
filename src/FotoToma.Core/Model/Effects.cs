using FotoToma.Core.Timing;

namespace FotoToma.Core.Model;

/// <summary>
/// Un efecto aplicado a un clip: un tipo (por ejemplo <c>fototoma.transform</c>) y sus parámetros.
/// La lista de tipos y su procesamiento llegan en la fase de efectos; el modelo ya los admite para que
/// el formato del proyecto no tenga que cambiar.
/// </summary>
public sealed class EffectInstance
{
    public string Id { get; set; } = "";

    public string Type { get; set; } = "";

    public bool Enabled { get; set; } = true;

    public Dictionary<string, Parameter> Parameters { get; set; } = [];

    public EffectInstance Clone()
    {
        EffectInstance copy = (EffectInstance)MemberwiseClone();
        copy.Parameters = Parameters.ToDictionary(p => p.Key, p => p.Value.Clone());
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

/// <summary>
/// Parámetro numérico animable. Sin keyframes vale <see cref="Value"/>; con keyframes se interpola.
/// Los vectores (posición x/y, color) se guardan como varios parámetros escalares.
/// </summary>
public sealed class Parameter
{
    public double Value { get; set; }

    /// <summary>Keyframes ordenados por tiempo. Vacío si el parámetro es constante.</summary>
    public List<Keyframe> Keyframes { get; set; } = [];

    /// <summary>
    /// Valor en el instante dado. Antes del primer keyframe vale el primero y después del último vale el
    /// último. Entre dos, la curva la decide la interpolación del keyframe de la izquierda.
    /// </summary>
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
