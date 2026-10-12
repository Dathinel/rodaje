namespace Rodaje.Analysis.Video;

/// <summary>Valores para los parámetros de <c>rodaje.color</c> (mismos nombres y rangos).</summary>
public readonly record struct ColorSettings(double Exposure, double Contrast, double Temperature, double Tint);

/// <summary>
/// Lleva lo medido por <see cref="ColorStatistics"/> a los parámetros del efecto de color, que trabaja en valores con
/// gamma: exposición <c>c·2^e</c>, contraste alrededor de 0,5 y ganancias <c>R·(1 + 0,2t)</c>, <c>G·(1 − 0,2m)</c>,
/// <c>B·(1 − 0,2t)</c>. Una ganancia lineal <c>k</c> es <c>k^(1/2,2)</c> con gamma.
/// </summary>
public static class AutoColor
{
    private const double Gamma = 2.2;

    /// <summary>
    /// Balance de blancos: temperatura y tinte que neutralizan el iluminante, y la exposición que deja la luminancia de un
    /// gris como estaba.
    /// </summary>
    public static ColorSettings WhiteBalance(ColorEstimate estimate)
    {
        if (!(estimate.Red > 0 && estimate.Green > 0 && estimate.Blue > 0))
        {
            return default;
        }

        // Ganancias lineales que llevan el iluminante a gris de la misma luminancia, pasadas a gamma.
        double gr = Math.Pow(1 / estimate.Red, 1 / Gamma);
        double gg = Math.Pow(1 / estimate.Green, 1 / Gamma);
        double gb = Math.Pow(1 / estimate.Blue, 1 / Gamma);
        double r = gr / gg;
        double b = gb / gg;
        // (1 + 0,2t) = r·G, (1 − 0,2t) = b·G y (1 − 0,2m) = G, con G = 2 / (r + b).
        double g = 2 / (r + b);
        double temperature = Math.Clamp(5 * ((r * g) - 1), -1, 1);
        double tint = Math.Clamp(5 * (1 - g), -1, 1);
        double applied = 1 - (0.2 * tint);
        double exposure = Math.Clamp(Math.Log2(gg / applied), -1, 1);
        return new ColorSettings(exposure, 0, temperature, tint);
    }

    /// <summary>
    /// Color automático: el balance de blancos más niveles. Exposición y contraste llevan los percentiles 0,5 y 99,5 de la
    /// luma a 0,03 y 0,95; el contraste no pasa de 0,8 y nunca baja (con el rango ya lleno solo se lleva el 99,5 a 0,95).
    /// </summary>
    public static ColorSettings Auto(ColorEstimate estimate)
    {
        ColorSettings balance = WhiteBalance(estimate);
        double high = Math.Clamp(estimate.High, 0.02, 1);
        double low = Math.Clamp(estimate.Low, 0, high - 0.01);
        // (hi·E − 0,5)(1 + k) = 0,45 y (lo·E − 0,5)(1 + k) = −0,47.
        double full = 0.9787 / (high + (0.957 * low));
        double contrast = Math.Clamp((0.92 / (full * (high - low))) - 1, 0, 0.8);
        double gain = contrast > 0 ? (0.5 - (0.01 / (1 + contrast))) / ((low + high) / 2) : 0.95 / high;
        double exposure = Math.Clamp(balance.Exposure + Math.Clamp(Math.Log2(gain), -1, 2), -4, 4);
        return balance with { Exposure = exposure, Contrast = contrast };
    }
}
