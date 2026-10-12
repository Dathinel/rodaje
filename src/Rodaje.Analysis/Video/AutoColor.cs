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
    /// Color automático: el balance de blancos más niveles. La exposición lleva el percentil 99,5 de la luma a 0,95
    /// (entre −1 y +1,5 pasos) y el contraste estira lo que quede sin recortar más allá de 0,03 y 0,97.
    /// </summary>
    public static ColorSettings Auto(ColorEstimate estimate)
    {
        ColorSettings balance = WhiteBalance(estimate);
        double high = Math.Max(estimate.High, 0.02);
        double levels = Math.Clamp(Math.Log2(0.95 / high), -1, 1.5);
        double exposure = Math.Clamp(balance.Exposure + levels, -4, 4);
        double factor = Math.Pow(2, levels);
        double low = estimate.Low * factor;
        high *= factor;
        double stretch = double.PositiveInfinity;
        if (high > 0.5)
        {
            stretch = Math.Min(stretch, 0.47 / (high - 0.5));
        }

        if (low < 0.5)
        {
            stretch = Math.Min(stretch, 0.47 / (0.5 - low));
        }

        double contrast = double.IsFinite(stretch) ? Math.Clamp(stretch - 1, 0, 0.6) : 0;
        return balance with { Exposure = exposure, Contrast = contrast };
    }
}
