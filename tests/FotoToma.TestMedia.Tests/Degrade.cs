namespace FotoToma.TestMedia.Tests;

/// <summary>Degradaciones sintéticas de un plano de luma, parecidas a las de un códec con pérdida.</summary>
internal static class Degrade
{
    public static void AddNoise(Span<byte> plane, int amplitude, int seed)
    {
        var random = new Random(seed);
        for (int i = 0; i < plane.Length; i++)
        {
            plane[i] = ClampByte(plane[i] + random.Next(-amplitude, amplitude + 1));
        }
    }

    public static byte[] BoxBlur(ReadOnlySpan<byte> plane, int width, int height, int radius)
    {
        var result = new byte[plane.Length];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int sum = 0;
                int count = 0;
                for (int dy = -radius; dy <= radius; dy++)
                {
                    for (int dx = -radius; dx <= radius; dx++)
                    {
                        int sx = Math.Clamp(x + dx, 0, width - 1);
                        int sy = Math.Clamp(y + dy, 0, height - 1);
                        sum += plane[sy * width + sx];
                        count++;
                    }
                }

                result[y * width + x] = (byte)(sum / count);
            }
        }

        return result;
    }

    /// <summary>DCT de 8 × 8 con la tabla de luma de JPEG escalada por <paramref name="scale"/>.</summary>
    public static void JpegLikeQuantize(Span<byte> plane, int width, int height, double scale)
    {
        int[] table =
        [
            16, 11, 10, 16, 24, 40, 51, 61, 12, 12, 14, 19, 26, 58, 60, 55,
            14, 13, 16, 24, 40, 57, 69, 56, 14, 17, 22, 29, 51, 87, 80, 62,
            18, 22, 37, 56, 68, 109, 103, 77, 24, 35, 55, 64, 81, 104, 113, 92,
            49, 64, 78, 87, 103, 121, 120, 101, 72, 92, 95, 98, 112, 100, 103, 99,
        ];
        var block = new double[64];
        var coefficients = new double[64];
        for (int by = 0; by + 8 <= height; by += 8)
        {
            for (int bx = 0; bx + 8 <= width; bx += 8)
            {
                for (int i = 0; i < 64; i++)
                {
                    block[i] = plane[(by + i / 8) * width + bx + i % 8] - 128.0;
                }

                Dct(block, coefficients, inverse: false);
                for (int i = 0; i < 64; i++)
                {
                    double q = table[i] * scale;
                    coefficients[i] = Math.Round(coefficients[i] / q) * q;
                }

                Dct(coefficients, block, inverse: true);
                for (int i = 0; i < 64; i++)
                {
                    plane[(by + i / 8) * width + bx + i % 8] = ClampByte((int)Math.Round(block[i] + 128));
                }
            }
        }
    }

    // Basis[k, n] = c(k) · cos((2n + 1) k π / 16) / 2: ortonormal, la inversa es la transpuesta.
    private static readonly double[,] Basis = CreateBasis();

    private static double[,] CreateBasis()
    {
        var basis = new double[8, 8];
        for (int k = 0; k < 8; k++)
        {
            for (int n = 0; n < 8; n++)
            {
                basis[k, n] = (k == 0 ? 1 / Math.Sqrt(2) : 1) * Math.Cos((2 * n + 1) * k * Math.PI / 16) / 2;
            }
        }

        return basis;
    }

    private static void Dct(double[] input, double[] output, bool inverse)
    {
        var temp = new double[64];
        for (int row = 0; row < 8; row++)
        {
            for (int k = 0; k < 8; k++)
            {
                double sum = 0;
                for (int n = 0; n < 8; n++)
                {
                    sum += input[row * 8 + n] * (inverse ? Basis[n, k] : Basis[k, n]);
                }

                temp[row * 8 + k] = sum;
            }
        }

        for (int col = 0; col < 8; col++)
        {
            for (int k = 0; k < 8; k++)
            {
                double sum = 0;
                for (int n = 0; n < 8; n++)
                {
                    sum += temp[n * 8 + col] * (inverse ? Basis[n, k] : Basis[k, n]);
                }

                output[k * 8 + col] = sum;
            }
        }
    }

    private static byte ClampByte(int value) => (byte)Math.Clamp(value, 0, 255);
}
