using System.Runtime.InteropServices;

namespace Rodaje.Core.Raw;

/// <summary>
/// Revelado de un mosaico de Bayer en la CPU, la referencia del shader <c>GpuRawDeveloper</c> (docs/hdr.md, fase D).
/// Los pasos son los de <c>dcraw_process</c> de LibRaw sin recuperar las luces: cada código se divide por el blanco, se
/// multiplica por su balance (el menor multiplicador es 1) y se recorta a 1; el interpolado es el de Malvar, He y Cutler
/// (5×5, con los bordes reflejados sin repetir el píxel del borde, que conserva el patrón); después la matriz de la cámara,
/// recortada a [0, 1]; la exposición (2^EV) y la curva, sin recortar por arriba (en rgba16f queda margen).
/// </summary>
public static class MosaicDeveloper
{
    /// <summary>Revela <paramref name="mosaic"/> (ancho × alto códigos, negro restado) en RGB (tres floats por píxel).</summary>
    /// <exception cref="ArgumentException">Tamaños que no cuadran o parámetros no válidos.</exception>
    public static void Develop(ReadOnlySpan<ushort> mosaic, int width, int height, RawDevelopParameters parameters, Span<float> rgb)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        parameters.Check();
        if (mosaic.Length < width * height)
        {
            throw new ArgumentException("El mosaico es más chico que el cuadro.", nameof(mosaic));
        }

        if (rgb.Length < width * height * 3)
        {
            throw new ArgumentException("La salida es más chica que el cuadro.", nameof(rgb));
        }

        float[] scaled = Scaled(mosaic, width, height, parameters);
        IReadOnlyList<float> m = parameters.Matrix;
        float gain = (float)parameters.Gain;
        RawOutputCurve curve = parameters.Curve;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                (float r, float g, float b) = Demosaic(scaled, width, height, x, y, parameters.Pattern);
                float or = Math.Clamp((m[0] * r) + (m[1] * g) + (m[2] * b), 0, 1) * gain;
                float og = Math.Clamp((m[3] * r) + (m[4] * g) + (m[5] * b), 0, 1) * gain;
                float ob = Math.Clamp((m[6] * r) + (m[7] * g) + (m[8] * b), 0, 1) * gain;
                int i = ((y * width) + x) * 3;
                rgb[i] = (float)Encode(or, curve);
                rgb[i + 1] = (float)Encode(og, curve);
                rgb[i + 2] = (float)Encode(ob, curve);
            }
        }
    }

    /// <summary>Como <see cref="Develop"/>, escribiendo rgba16f (alfa 1) fila por fila con <paramref name="stride"/> bytes por fila.</summary>
    public static void DevelopRgba16F(ReadOnlySpan<ushort> mosaic, int width, int height, RawDevelopParameters parameters, Span<byte> output, int stride)
    {
        if (stride < width * 8 || output.Length < ((long)stride * (height - 1)) + (width * 8))
        {
            throw new ArgumentException("La salida es más chica que el cuadro.", nameof(output));
        }

        var rgb = new float[width * height * 3];
        Develop(mosaic, width, height, parameters, rgb);
        for (int y = 0; y < height; y++)
        {
            Span<Half> row = MemoryMarshal.Cast<byte, Half>(output.Slice(y * stride, width * 8));
            for (int x = 0, s = y * width * 3, t = 0; x < width; x++, s += 3, t += 4)
            {
                row[t] = (Half)rgb[s];
                row[t + 1] = (Half)rgb[s + 1];
                row[t + 2] = (Half)rgb[s + 2];
                row[t + 3] = Half.One;
            }
        }
    }

    /// <summary>La curva aplicada a un valor lineal (recortado por abajo, no por arriba).</summary>
    public static double Encode(double linear, RawOutputCurve curve)
    {
        double v = Math.Max(linear, 0);
        return curve switch
        {
            RawOutputCurve.Rec709 => v < 0.018 ? 4.5 * v : (1.099 * Math.Pow(v, 0.45)) - 0.099,
            RawOutputCurve.Srgb => v <= 0.0031308 ? 12.92 * v : (1.055 * Math.Pow(v, 1 / 2.4)) - 0.055,
            _ => v,
        };
    }

    /// <summary>Coordenada reflejada en el borde sin repetir el píxel del borde: −1 → 1, n → n − 2. Conserva la paridad.</summary>
    public static int Mirror(int i, int n)
    {
        if (i < 0)
        {
            i = -i;
        }

        if (i >= n)
        {
            i = (2 * n) - 2 - i;
        }

        return Math.Clamp(i, 0, n - 1);
    }

    /// <summary>Cada código dividido por el blanco, por su multiplicador y recortado a 1.</summary>
    private static float[] Scaled(ReadOnlySpan<ushort> mosaic, int width, int height, RawDevelopParameters parameters)
    {
        float[] multipliers = parameters.NormalizedMultipliers();
        float white = parameters.White;
        var scaled = new float[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = (y * width) + x;
                scaled[i] = Math.Min(mosaic[i] / white * multipliers[parameters.Pattern.ChannelAt(x, y)], 1);
            }
        }

        return scaled;
    }

    /// <summary>Malvar, He y Cutler (2004): filtros de 5×5 con pesos en octavos; cada canal queda en [0, 1].</summary>
    private static (float R, float G, float B) Demosaic(float[] s, int width, int height, int x, int y, BayerPattern pattern)
    {
        float At(int dx, int dy) => s[(Mirror(y + dy, height) * width) + Mirror(x + dx, width)];

        float c = At(0, 0);
        float hn = At(-1, 0) + At(1, 0);
        float vn = At(0, -1) + At(0, 1);
        float diag = At(-1, -1) + At(1, -1) + At(-1, 1) + At(1, 1);
        float h2 = At(-2, 0) + At(2, 0);
        float v2 = At(0, -2) + At(0, 2);
        int channel = pattern.ChannelAt(x, y);
        float r, g, b;
        if (channel == BayerPattern.Green)
        {
            // Vecinos de al lado de un color y de arriba y abajo del otro.
            float horizontal = ((5 * c) + (4 * hn) - diag + (0.5f * v2) - h2) / 8;
            float vertical = ((5 * c) + (4 * vn) - diag + (0.5f * h2) - v2) / 8;
            g = c;
            if (pattern.ChannelAt(x + 1, y) == BayerPattern.Red)
            {
                r = horizontal;
                b = vertical;
            }
            else
            {
                r = vertical;
                b = horizontal;
            }
        }
        else
        {
            g = ((4 * c) + (2 * (hn + vn)) - h2 - v2) / 8;
            float other = ((6 * c) + (2 * diag) - (1.5f * (h2 + v2))) / 8;
            (r, b) = channel == BayerPattern.Red ? (c, other) : (other, c);
        }

        return (Math.Clamp(r, 0, 1), Math.Clamp(g, 0, 1), Math.Clamp(b, 0, 1));
    }
}
