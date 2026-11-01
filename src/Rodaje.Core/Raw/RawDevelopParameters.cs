namespace Rodaje.Core.Raw;

/// <summary>Curva de transferencia con la que sale el revelado.</summary>
public enum RawOutputCurve
{
    Linear = 0,
    Rec709 = 1,
    Srgb = 2,
}

/// <summary>
/// Lo que hace falta para revelar un mosaico de Bayer (códigos con el negro ya restado) hasta RGB de salida: el patrón, el
/// blanco, el balance, la matriz de color de la cámara, la exposición y la curva. Es el contrato entre <c>modulo-raw</c>
/// (que lo saca de LibRaw) y los reveladores de <see cref="MosaicDeveloper"/> (CPU) y <c>GpuRawDeveloper</c> (GPU).
/// </summary>
public sealed record RawDevelopParameters
{
    public const double MaxExposure = 6;

    public required BayerPattern Pattern { get; init; }

    /// <summary>Código del blanco con el negro restado (1 a 65 535): lo que llega a 1,0 antes del balance.</summary>
    public required int White { get; init; }

    /// <summary>Multiplicadores R, G, B del balance de blancos, mayores que 0; se normalizan para que el menor sea 1.</summary>
    public required IReadOnlyList<float> Multipliers { get; init; }

    /// <summary>Matriz 3×3 por filas de RGB de la cámara (tras el balance) a RGB lineal de salida.</summary>
    public required IReadOnlyList<float> Matrix { get; init; }

    /// <summary>Pasos (EV) en luz lineal, después de la matriz.</summary>
    public double Exposure { get; init; }

    public RawOutputCurve Curve { get; init; } = RawOutputCurve.Linear;

    /// <summary>2^<see cref="Exposure"/>.</summary>
    public double Gain => Math.Pow(2, Exposure);

    /// <summary>Un texto con lo que está mal, o <c>null</c> si todo vale.</summary>
    public string? Validate()
    {
        if (!Pattern.IsValid)
        {
            return "el patrón de Bayer no es un rojo, un azul y dos verdes";
        }

        if (White is < 1 or > 65_535)
        {
            return "el blanco va de 1 a 65 535";
        }

        if (Multipliers is not { Count: 3 } || Multipliers.Any(m => !float.IsFinite(m) || m <= 0))
        {
            return "hacen falta tres multiplicadores de balance mayores que 0";
        }

        if (Matrix is not { Count: 9 } || Matrix.Any(v => !float.IsFinite(v)))
        {
            return "la matriz de color necesita nueve números";
        }

        if (!double.IsFinite(Exposure) || Math.Abs(Exposure) > MaxExposure)
        {
            return $"la exposición va de −{MaxExposure} a +{MaxExposure} pasos";
        }

        return Curve is RawOutputCurve.Linear or RawOutputCurve.Rec709 or RawOutputCurve.Srgb ? null : "curva desconocida";
    }

    /// <exception cref="ArgumentException">Algún parámetro no vale.</exception>
    public void Check()
    {
        if (Validate() is { } problem)
        {
            throw new ArgumentException($"Parámetros de revelado no válidos: {problem}.");
        }
    }

    /// <summary>Los multiplicadores con el menor en 1 (como <c>scale_colors</c> de LibRaw sin recuperar las luces).</summary>
    public float[] NormalizedMultipliers()
    {
        float min = Math.Min(Multipliers[0], Math.Min(Multipliers[1], Multipliers[2]));
        return [Multipliers[0] / min, Multipliers[1] / min, Multipliers[2] / min];
    }
}
