namespace Rodaje.App.Services.Inspector;

/// <summary>
/// Las cuentas de los cuentagotas de color, sin WinUI: el balance de blancos sobre un punto que debería ser gris y el tono
/// que elige el calificador HSL. Siguen las fórmulas de «Color y LUT» (docs/arquitectura/render.md).
/// </summary>
public static class ColorPickMath
{
    private const double Kr = 0.2126;
    private const double Kg = 0.7152;
    private const double Kb = 0.0722;

    /// <summary>
    /// Temperatura y tinte que dejan gris el color tomado (sin la corrección), con la exposición, el contraste y la
    /// saturación que ya tiene el efecto: las ganancias van después de ellos y antes de luces y sombras, que suman igual a
    /// los tres canales. Recortados a ±1.
    /// </summary>
    public static (double Temperature, double Tint) WhiteBalance((double R, double G, double B) sample, double exposure, double contrast,
        double saturation)
    {
        double gain = Math.Pow(2, exposure);
        double r = Contrast(sample.R * gain, contrast), g = Contrast(sample.G * gain, contrast), b = Contrast(sample.B * gain, contrast);
        double y = (Kr * r) + (Kg * g) + (Kb * b);
        r = y + ((r - y) * (1 + saturation));
        g = y + ((g - y) * (1 + saturation));
        b = y + ((b - y) * (1 + saturation));
        if (r + b <= 1e-6 || g <= 1e-6)
        {
            return (0, 0);
        }

        double temperature = Math.Clamp((b - r) / (0.2 * (r + b)), -1, 1);
        double gray = r * (1 + (0.2 * temperature));
        double tint = Math.Clamp((1 - (gray / g)) / 0.2, -1, 1);
        return (temperature, tint);
    }

    /// <summary>Tono de HSV en grados (0 rojo, 120 verde, 240 azul) y saturación de HSL del color tomado.</summary>
    public static (double HueDegrees, double Saturation, double Luma) Qualify((double R, double G, double B) c)
    {
        double high = Math.Max(c.R, Math.Max(c.G, c.B));
        double low = Math.Min(c.R, Math.Min(c.G, c.B));
        double d = high - low;
        double hue = 0;
        if (d > 1e-5)
        {
            hue = high == c.R ? (c.G - c.B) / d : high == c.G ? 2 + ((c.B - c.R) / d) : 4 + ((c.R - c.G) / d);
            hue = ((hue / 6) + 1) % 1 * 360;
        }

        double l = 0.5 * (high + low);
        double s = Math.Clamp(d / Math.Max(1 - Math.Abs((2 * l) - 1), 1e-4), 0, 1);
        return (hue, s, (Kr * c.R) + (Kg * c.G) + (Kb * c.B));
    }

    private static double Contrast(double v, double contrast) => ((v - 0.5) * (1 + contrast)) + 0.5;
}
