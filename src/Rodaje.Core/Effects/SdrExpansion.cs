namespace Rodaje.Core.Effects;

/// <summary>
/// «Expandir a HDR» de un clip SDR en una secuencia HDR: la curva inversa de BT.2446-1 método A (SDR de 203 nits a HDR de
/// <c>peakNits</c>) anclada en el gris del 18 %. Por debajo del gris no cambia nada; por encima, el realce sigue la forma de
/// la curva hasta llevar el blanco al pico. El método literal multiplica el gris por 2,4 y haría que un clip SDR no se viera
/// igual que en una secuencia SDR. Referencia en la CPU de <c>ExpandSdr</c> en <c>RenderShaders</c>.
/// </summary>
public static class SdrExpansion
{
    public const double MinPeakNits = 203;

    public const double MaxPeakNits = 1000;

    /// <summary>Blanco SDR en una secuencia HDR (BT.2408).</summary>
    public const double WhiteNits = 203;

    /// <summary>Luminancia lineal del gris que no se mueve.</summary>
    public const double Gray = 0.18;

    /// <summary>El pico que usa el shader: 0 elige 1000 nits; el resto se acota a 203–1000.</summary>
    public static double PeakFor(double referenceNits) =>
        referenceNits > 0 && double.IsFinite(referenceNits) ? Math.Clamp(referenceNits, MinPeakNits, MaxPeakNits) : MaxPeakNits;

    /// <summary>Luminancia lineal (1,0 = 203 nits) expandida hacia <paramref name="peakNits"/>; la entrada se toma en [0, 1].</summary>
    public static double Expand(double luminance, double peakNits)
    {
        double y = Math.Clamp(luminance, 0, 1);
        double anchor = Normalized(Gray, peakNits);
        double s = Math.Clamp((Normalized(y, peakNits) - anchor) / (1 - anchor), 0, 1);
        return y * (1 + ((peakNits / WhiteNits) - 1) * s);
    }

    /// <summary>
    /// La curva inversa de BT.2446-1 método A, de luminancia lineal SDR en [0, 1] a luminancia lineal relativa al pico (0 → 0,
    /// 1 → 1): luma en gamma 2,4, logarítmica con ρ del SDR, la inversa de la curva de tono por tramos y exponencial con ρ del HDR.
    /// </summary>
    public static double Normalized(double luminance, double peakNits)
    {
        double y = Math.Clamp(luminance, 0, 1);
        double rhoSdr = 1 + (32 * Math.Pow(WhiteNits / 10000, 1 / 2.4));
        double rhoHdr = 1 + (32 * Math.Pow(peakNits / 10000, 1 / 2.4));
        double yc = Math.Log(1 + ((rhoSdr - 1) * Math.Pow(y, 1 / 2.4))) / Math.Log(rhoSdr);
        double yp = yc <= 0.79688 ? yc / 1.0770
            : yc < 0.99545 ? (2.7811 - Math.Sqrt(Math.Max((2.7811 * 2.7811) - (4 * 1.1510 * (0.6302 + yc)), 0))) / (2 * 1.1510)
            : (2 * yc) - 1;
        double yHdr = (Math.Pow(rhoHdr, yp) - 1) / (rhoHdr - 1);
        return Math.Pow(Math.Clamp(yHdr, 0, 1), 2.4);
    }
}
