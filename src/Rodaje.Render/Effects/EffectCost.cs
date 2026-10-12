using Rodaje.Core.Model;

namespace Rodaje.Render.Effects;

/// <summary>Cuánto pesa un efecto en la GPU, para el indicador de la pila.</summary>
public enum CostLevel
{
    /// <summary>Va fusionado en el shader del color o es una pasada simple: casi gratis.</summary>
    Low,

    /// <summary>Una o dos pasadas del tamaño de la capa.</summary>
    Medium,

    /// <summary>Varias pasadas o muchas muestras: el que más resta en 4K.</summary>
    High,
}

/// <summary>Estimación del costo de un efecto en milisegundos de GPU y su nivel.</summary>
public readonly record struct EffectCostEstimate(double Milliseconds, CostLevel Level);

/// <summary>
/// Costo por efecto medido en una RTX 3050 Laptop con una capa 1080p (<c>ImageEffectPerformanceTests</c>), escalado por
/// los píxeles de la secuencia. Los efectos del intermedio pagan además una vez el intermedio de la capa.
/// </summary>
public static class EffectCost
{
    /// <summary>El intermedio de la capa (lo pagan una vez los efectos que van en él), en ms a 1080p.</summary>
    public const double IntermediateMs1080 = 0.06;

    private const double Pixels1080 = 1920.0 * 1080;

    /// <param name="type">Tipo del efecto.</param>
    /// <param name="option">Una opción de elección del efecto por su nombre (el tipo de desenfoque); vacía si no hay.</param>
    /// <param name="width">Ancho de la secuencia.</param>
    /// <param name="height">Alto de la secuencia.</param>
    public static EffectCostEstimate Estimate(string type, Func<string, string?> option, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(option);
        double ms1080 = type switch
        {
            BuiltInEffects.Color or BuiltInEffects.ColorWheels or BuiltInEffects.Vignette or BuiltInEffects.ChromaKey => 0.01,
            BuiltInEffects.Curves => 0.05,
            BuiltInEffects.HslQualifier => 0.08,
            BuiltInEffects.Mask => 0.03,
            BuiltInEffects.Blur => option(BuiltInEffects.BlurOptions.Kind) switch
            {
                "lens" => 0.22,
                "directional" => 0.9,
                "radial" => 0.95,
                _ => 0.07,
            },
            BuiltInEffects.Sharpen => 0.35,
            BuiltInEffects.Grain => 0.3,
            BuiltInEffects.Glow => 0.22,
            BuiltInEffects.ChromaticAberration or BuiltInEffects.Mirror or BuiltInEffects.LensDistortion => 0.09,
            BuiltInEffects.Reframe360 => 0.15,
            BuiltInEffects.Denoise => 0.55,
            _ => 0,
        };
        double ms = ms1080 * Math.Max(1, (double)width * height) / Pixels1080;
        CostLevel level = ms1080 < 0.1 ? CostLevel.Low : ms1080 < 0.4 ? CostLevel.Medium : CostLevel.High;
        return new EffectCostEstimate(ms, level);
    }
}
