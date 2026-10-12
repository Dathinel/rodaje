using Rodaje.Analysis.Video;
using Xunit.Abstractions;

namespace Rodaje.Analysis.Tests.Video;

public sealed class AutoColorTests(ITestOutputHelper output)
{
    private const int Side = 320;
    private const int Patch = 10;

    /// <summary>
    /// Carta sintética: parches con R, G y B elegidos por separado con la misma distribución (en promedio la escena es
    /// gris, como supone el balance automático) y una fila de grises.
    /// </summary>
    private static double[] Scene(int seed, double low = 0.08, double high = 0.8)
    {
        var random = new Random(seed);
        double[] rgb = new double[Side * Side * 3];
        int patches = Side / Patch;
        (double R, double G, double B) last = default;
        for (int py = 0; py < patches; py++)
        {
            for (int px = 0; px < patches; px++)
            {
                double r, g, b;
                if (py == patches - 1)
                {
                    r = g = b = low + ((high - low) * px / (patches - 1));
                }
                else if (px % 3 == 0)
                {
                    r = low + ((high - low) * random.NextDouble());
                    g = low + ((high - low) * random.NextDouble());
                    b = low + ((high - low) * random.NextDouble());
                    last = (r, g, b);
                }
                else
                {
                    // Las rotaciones del parche anterior: cada canal ve los mismos valores y la escena es gris en promedio.
                    (r, g, b) = (last.G, last.B, last.R);
                    last = (r, g, b);
                }

                for (int y = py * Patch; y < (py + 1) * Patch; y++)
                {
                    for (int x = px * Patch; x < (px + 1) * Patch; x++)
                    {
                        int i = ((y * Side) + x) * 3;
                        rgb[i] = r;
                        rgb[i + 1] = g;
                        rgb[i + 2] = b;
                    }
                }
            }
        }

        return rgb;
    }

    /// <summary>Dominante en luz lineal, con ganancias normalizadas a luminancia 1 (un gris conserva su luminancia).</summary>
    private static double[] Cast(double[] scene, double r, double g, double b)
    {
        double y = (0.2126 * r) + (0.7152 * g) + (0.0722 * b);
        double[] gains = [r / y, g / y, b / y];
        double[] cast = new double[scene.Length];
        for (int i = 0; i < scene.Length; i++)
        {
            cast[i] = Srgb.FromLinear(Math.Min(1, Srgb.ToLinear(scene[i]) * gains[i % 3]));
        }

        return cast;
    }

    private static byte[] Bgra(double[] rgb)
    {
        byte[] bytes = new byte[Side * Side * 4];
        for (int p = 0; p < Side * Side; p++)
        {
            bytes[(p * 4) + 2] = Quantize(rgb[p * 3]);
            bytes[(p * 4) + 1] = Quantize(rgb[(p * 3) + 1]);
            bytes[p * 4] = Quantize(rgb[(p * 3) + 2]);
            bytes[(p * 4) + 3] = 255;
        }

        return bytes;
    }

    private static byte Quantize(double v) => (byte)Math.Clamp(Math.Round(v * 255), 0, 255);

    private static ColorEstimate Measure(double[] rgb)
    {
        var statistics = new ColorStatistics();
        statistics.PushBgra(Bgra(rgb), Side, Side, Side * 4);
        return statistics.Estimate();
    }

    /// <summary>Las fórmulas de <c>rodaje.color</c> (docs/arquitectura/render.md), con saturación, altas y sombras en 0.</summary>
    private static double[] Apply(double[] rgb, ColorSettings s)
    {
        double[] gains = [1 + (0.2 * s.Temperature), 1 - (0.2 * s.Tint), 1 - (0.2 * s.Temperature)];
        double exposure = Math.Pow(2, s.Exposure);
        double[] result = new double[rgb.Length];
        for (int i = 0; i < rgb.Length; i++)
        {
            double c = Quantize(rgb[i]) / 255.0 * exposure;
            c = ((c - 0.5) * (1 + s.Contrast)) + 0.5;
            result[i] = Math.Clamp(c * gains[i % 3], 0, 1);
        }

        return result;
    }

    private static (double L, double A, double B) Lab(double r, double g, double b)
    {
        double lr = Srgb.ToLinear(r), lg = Srgb.ToLinear(g), lb = Srgb.ToLinear(b);
        double x = ((0.4124 * lr) + (0.3576 * lg) + (0.1805 * lb)) / 0.95047;
        double y = (0.2126 * lr) + (0.7152 * lg) + (0.0722 * lb);
        double z = ((0.0193 * lr) + (0.1192 * lg) + (0.9505 * lb)) / 1.08883;
        static double F(double t) => t > 216.0 / 24389 ? Math.Cbrt(t) : ((24389.0 / 27 * t) + 16) / 116;
        return ((116 * F(y)) - 16, 500 * (F(x) - F(y)), 200 * (F(y) - F(z)));
    }

    /// <summary>ΔE*ab (CIE 1976) medio de toda la carta y máximo en los grises.</summary>
    private static (double Mean, double GrayWorst) DeltaE(double[] reference, double[] corrected)
    {
        double sum = 0, grayWorst = 0;
        int n = Side * Side;
        for (int p = 0; p < n; p++)
        {
            var a = Lab(Quantize(reference[p * 3]) / 255.0, Quantize(reference[(p * 3) + 1]) / 255.0, Quantize(reference[(p * 3) + 2]) / 255.0);
            var b = Lab(corrected[p * 3], corrected[(p * 3) + 1], corrected[(p * 3) + 2]);
            double e = Math.Sqrt(((a.L - b.L) * (a.L - b.L)) + ((a.A - b.A) * (a.A - b.A)) + ((a.B - b.B) * (a.B - b.B)));
            sum += e;
            if (p / Side >= Side - Patch)
            {
                grayWorst = Math.Max(grayWorst, e);
            }
        }

        return (sum / n, grayWorst);
    }

    /// <summary>Criterio de la fase 7: una dominante sintética queda corregida a ΔE ≤ 3.</summary>
    [Theory]
    [InlineData("cálida", 1.25, 1.0, 0.75, 1)]
    [InlineData("fría", 0.8, 1.0, 1.3, 2)]
    [InlineData("verde", 0.9, 1.15, 0.9, 3)]
    [InlineData("magenta", 1.12, 0.88, 1.12, 4)]
    [InlineData("tungsteno", 1.4, 1.0, 0.6, 5)]
    public void Synthetic_cast_is_corrected_to_delta_e_3(string name, double r, double g, double b, int seed)
    {
        double[] scene = Scene(seed);
        double[] cast = Cast(scene, r, g, b);
        ColorSettings settings = AutoColor.WhiteBalance(Measure(cast));
        (double before, double grayBefore) = DeltaE(scene, [.. cast.Select(v => Quantize(v) / 255.0)]);
        (double after, double grayAfter) = DeltaE(scene, Apply(cast, settings));
        output.WriteLine($"{name}: ΔE medio {before:F1} → {after:F2}, grises {grayBefore:F1} → {grayAfter:F2} " +
            $"(temperatura {settings.Temperature:F3}, tinte {settings.Tint:F3}, exposición {settings.Exposure:F3})");
        Assert.True(after <= 3, $"ΔE medio {after:F2}");
        Assert.True(grayAfter <= 3, $"ΔE en los grises {grayAfter:F2}");
    }

    [Fact]
    public void A_neutral_scene_is_left_almost_untouched()
    {
        ColorSettings settings = AutoColor.WhiteBalance(Measure(Scene(6)));
        output.WriteLine($"neutra: temperatura {settings.Temperature:F4}, tinte {settings.Tint:F4}, exposición {settings.Exposure:F4}");
        Assert.InRange(settings.Temperature, -0.02, 0.02);
        Assert.InRange(settings.Tint, -0.02, 0.02);
        Assert.InRange(settings.Exposure, -0.02, 0.02);
    }

    [Fact]
    public void Auto_color_balances_and_stretches_a_dull_dark_clip()
    {
        double[] scene = Scene(7, low: 0.15, high: 0.5);
        double[] cast = Cast(scene, 1.2, 1.0, 0.8);
        ColorEstimate estimate = Measure(cast);
        ColorSettings settings = AutoColor.Auto(estimate);
        double[] corrected = Apply(cast, settings);
        double[] luma = new double[Side * Side];
        for (int p = 0; p < luma.Length; p++)
        {
            luma[p] = (0.2126 * corrected[p * 3]) + (0.7152 * corrected[(p * 3) + 1]) + (0.0722 * corrected[(p * 3) + 2]);
        }

        Array.Sort(luma);
        double low = luma[(int)(0.005 * luma.Length)];
        double high = luma[(int)(0.995 * luma.Length)];
        // El tono se compara con la carta llevada a los mismos niveles: el balance tiene que seguir neutro.
        ColorSettings balanceOnly = AutoColor.WhiteBalance(estimate);
        output.WriteLine($"luma de {estimate.Low:F2}–{estimate.High:F2} a {low:F2}–{high:F2}; exposición {settings.Exposure:F2}, " +
            $"contraste {settings.Contrast:F2}, temperatura {settings.Temperature:F3} (balance solo {balanceOnly.Temperature:F3})");
        Assert.True(high >= 0.85, $"{high:F2}");
        Assert.True(low <= 0.15, $"{low:F2}");
        Assert.Equal(balanceOnly.Temperature, settings.Temperature);
        Assert.Equal(balanceOnly.Tint, settings.Tint);
    }

    [Fact]
    public void The_estimate_ignores_clipped_highlights_and_takes_the_median_across_frames()
    {
        var statistics = new ColorStatistics();
        double[] warm = Cast(Scene(8), 1.25, 1.0, 0.75);
        statistics.PushBgra(Bgra(warm), Side, Side, Side * 4);
        statistics.PushBgra(Bgra(warm), Side, Side, Side * 4);
        // Un cuadro raro (todo azul) no mueve la mediana de tres.
        double[] odd = Cast(Scene(9), 0.6, 0.9, 1.6);
        statistics.PushBgra(Bgra(odd), Side, Side, Side * 4);
        ColorEstimate estimate = statistics.Estimate();
        Assert.Equal(3, statistics.Frames);
        Assert.True(estimate.Red > estimate.Blue * 1.4, $"{estimate.Red:F2} / {estimate.Blue:F2}");
    }
}
