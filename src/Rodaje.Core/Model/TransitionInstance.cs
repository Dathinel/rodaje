using System.Text.Json.Serialization;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Model;

/// <summary>Dónde queda una transición respecto de su corte (formato 12).</summary>
public enum TransitionAlignment
{
    /// <summary>La mitad antes del corte y la mitad después.</summary>
    Center,

    /// <summary>Toda antes del corte: termina donde termina el clip que sale.</summary>
    Before,

    /// <summary>Toda después del corte: empieza donde empieza el clip que entra.</summary>
    After,
}

/// <summary>
/// Transición en el corte entre dos clips contiguos de una pista: dura <see cref="Duration"/>, repartida alrededor del corte
/// según <see cref="Alignment"/>. Los clips no se solapan; cada lado usa el material que sobra más allá de su borde y, si no
/// hay, congela el cuadro del borde.
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

    /// <summary>Centrada (por defecto), al final del clip que sale o al inicio del que entra.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public TransitionAlignment Alignment { get; set; }

    /// <summary>
    /// Curva del avance, con las fórmulas de los keyframes (<see cref="KeyframeMath.Ease"/>); nula es lineal. El avance
    /// se recorta a 0–1 y vale exactamente 0 y 1 en los extremos.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Interpolation? Curve { get; set; }

    /// <summary>Puntos de la Bézier cuando <see cref="Curve"/> es <see cref="Interpolation.Bezier"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BezierEase? Ease { get; set; }

    /// <summary>Parámetros numéricos constantes (sin keyframes).</summary>
    public Dictionary<string, double> Parameters { get; set; } = [];

    /// <summary>Opciones no numéricas, por ejemplo la referencia del preajuste de un paquete.</summary>
    public Dictionary<string, string> Options { get; set; } = [];

    /// <summary>Parte de la duración antes del corte; centrada, la mitad (lo que sobra en ticks impares va después).</summary>
    [JsonIgnore]
    public Time Before => Alignment switch
    {
        TransitionAlignment.Before => Duration,
        TransitionAlignment.After => Time.Zero,
        _ => Time.FromTicks(Duration.Ticks / 2),
    };

    [JsonIgnore]
    public Time After => Duration - Before;

    /// <summary>El avance con la curva, de 0 a 1, para un avance del tiempo <paramref name="t"/> de 0 a 1.</summary>
    public double Eased(double t) => EaseProgress(Curve, Ease, t);

    /// <summary>
    /// Avance suavizado: exacto en los extremos (0 antes del inicio, 1 al final) y recortado a 0–1 en medio, porque una
    /// mezcla no se pasa de sus dos imágenes. «Mantener» cambia de golpe en la mitad.
    /// </summary>
    public static double EaseProgress(Interpolation? curve, BezierEase? ease, double t)
    {
        if (!(t > 0))
        {
            return 0;
        }

        if (t >= 1)
        {
            return 1;
        }

        if (curve is not { } c || c == Interpolation.Linear)
        {
            return t;
        }

        if (c == Interpolation.Hold)
        {
            return t < 0.5 ? 0 : 1;
        }

        double value = KeyframeMath.Ease(c, ease, t);
        return double.IsFinite(value) ? Math.Clamp(value, 0, 1) : t;
    }

    public TransitionInstance Clone()
    {
        TransitionInstance copy = (TransitionInstance)MemberwiseClone();
        copy.Parameters = new Dictionary<string, double>(Parameters);
        copy.Options = new Dictionary<string, string>(Options);
        return copy;
    }
}
