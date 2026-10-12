namespace Rodaje.Analysis.Video;

/// <summary>Lo que se midió del color de un clip.</summary>
/// <param name="Red">Iluminante estimado en luz lineal, normalizado a luminancia 1 (BT.709).</param>
/// <param name="Green">Componente verde del iluminante.</param>
/// <param name="Blue">Componente azul del iluminante.</param>
/// <param name="Low">Luma (R'G'B' con pesos BT.709, 0–1) en el percentil 0,5.</param>
/// <param name="Median">Luma en el percentil 50.</param>
/// <param name="High">Luma en el percentil 99,5.</param>
public readonly record struct ColorEstimate(double Red, double Green, double Blue, double Low, double Median, double High)
{
    public static readonly ColorEstimate Neutral = new(1, 1, 1, 0, 0.5, 1);
}

/// <summary>
/// Junta la estadística de color de varios cuadros BGRA de 8 bits (sRGB). Por cuadro estima el iluminante con
/// <i>shades of gray</i> (norma de Minkowski p = 6, Finlayson y Trezzi 2004) y con <i>gray edge</i> de primer orden
/// (van de Weijer, Gevers y Gijsenij 2007), en luz lineal y sin los píxeles recortados; el resultado es la media
/// geométrica de los dos, y entre cuadros la mediana (suavizado en el tiempo, sin que un cuadro raro mande).
/// </summary>
public sealed class ColorStatistics
{
    private const double P = 6;
    private const int Bins = 1024;

    private static readonly double[] s_linear = BuildLinear();
    private static readonly double[] s_linearPow = [.. s_linear.Select(v => Math.Pow(v, P))];

    private readonly List<(double R, double G, double B)> _estimates = [];
    private readonly long[] _histogram = new long[Bins];
    private long _samples;

    public int Frames => _estimates.Count;

    /// <summary>Suma un cuadro BGRA (B, G, R, A por píxel) de <paramref name="width"/>×<paramref name="height"/> con <paramref name="stride"/> bytes por fila.</summary>
    public void PushBgra(ReadOnlySpan<byte> bgra, int width, int height, int stride)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 2);
        if (stride < width * 4 || bgra.Length < ((long)(height - 1) * stride) + (width * 4))
        {
            throw new ArgumentException("El cuadro es más chico que su tamaño declarado.", nameof(bgra));
        }

        double sr = 0, sg = 0, sb = 0;
        double er = 0, eg = 0, eb = 0;
        long count = 0, edges = 0;
        for (int y = 0; y < height - 1; y++)
        {
            ReadOnlySpan<byte> row = bgra.Slice(y * stride, width * 4);
            ReadOnlySpan<byte> below = bgra.Slice((y + 1) * stride, width * 4);
            for (int x = 0; x < width - 1; x++)
            {
                int i = x * 4;
                byte b = row[i], g = row[i + 1], r = row[i + 2];
                _histogram[(int)(((0.2126 * r) + (0.7152 * g) + (0.0722 * b)) * (Bins - 1) / 255.0)]++;
                _samples++;
                if (Clipped(r, g, b) || Clipped(row[i + 6], row[i + 5], row[i + 4]) || Clipped(below[i + 2], below[i + 1], below[i]))
                {
                    continue;
                }

                sr += s_linearPow[r];
                sg += s_linearPow[g];
                sb += s_linearPow[b];
                count++;
                double dr = Gradient(r, row[i + 6], below[i + 2]);
                double dg = Gradient(g, row[i + 5], below[i + 1]);
                double db = Gradient(b, row[i + 4], below[i]);
                if (dr + dg + db > 1e-4)
                {
                    er += Math.Pow(dr, P);
                    eg += Math.Pow(dg, P);
                    eb += Math.Pow(db, P);
                    edges++;
                }
            }
        }

        if (count < 64)
        {
            return;
        }

        (double R, double G, double B) gray = Normalize(Math.Pow(sr / count, 1 / P), Math.Pow(sg / count, 1 / P), Math.Pow(sb / count, 1 / P));
        if (edges >= 64)
        {
            (double R, double G, double B) edge = Normalize(Math.Pow(er / edges, 1 / P), Math.Pow(eg / edges, 1 / P), Math.Pow(eb / edges, 1 / P));
            gray = Normalize(Math.Sqrt(gray.R * edge.R), Math.Sqrt(gray.G * edge.G), Math.Sqrt(gray.B * edge.B));
        }

        if (double.IsFinite(gray.R) && double.IsFinite(gray.G) && double.IsFinite(gray.B))
        {
            _estimates.Add(gray);
        }
    }

    /// <summary>El iluminante y los percentiles de todo lo sumado; neutro si no hubo cuadros útiles.</summary>
    public ColorEstimate Estimate()
    {
        (double r, double g, double b) = _estimates.Count == 0
            ? (1, 1, 1)
            : Normalize(Median(_estimates.Select(e => e.R)), Median(_estimates.Select(e => e.G)), Median(_estimates.Select(e => e.B)));
        if (_samples == 0)
        {
            return ColorEstimate.Neutral with { Red = r, Green = g, Blue = b };
        }

        return new ColorEstimate(r, g, b, Percentile(0.005), Percentile(0.5), Percentile(0.995));
    }

    private double Percentile(double fraction)
    {
        long target = (long)Math.Ceiling(fraction * _samples);
        long seen = 0;
        for (int i = 0; i < Bins; i++)
        {
            seen += _histogram[i];
            if (seen >= Math.Max(1, target))
            {
                return (i + 0.5) / Bins;
            }
        }

        return 1;
    }

    private static bool Clipped(byte r, byte g, byte b) => r >= 250 || g >= 250 || b >= 250 || (r <= 3 && g <= 3 && b <= 3);

    private static double Gradient(byte c, byte right, byte down)
    {
        double dx = s_linear[right] - s_linear[c];
        double dy = s_linear[down] - s_linear[c];
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    private static (double R, double G, double B) Normalize(double r, double g, double b)
    {
        double y = (0.2126 * r) + (0.7152 * g) + (0.0722 * b);
        return y > 0 ? (r / y, g / y, b / y) : (1, 1, 1);
    }

    private static double Median(IEnumerable<double> values)
    {
        double[] sorted = [.. values.Order()];
        int n = sorted.Length;
        return n % 2 == 1 ? sorted[n / 2] : (sorted[(n / 2) - 1] + sorted[n / 2]) / 2;
    }

    private static double[] BuildLinear()
    {
        double[] table = new double[256];
        for (int i = 0; i < 256; i++)
        {
            table[i] = Srgb.ToLinear(i / 255.0);
        }

        return table;
    }
}

/// <summary>La curva de transferencia sRGB (IEC 61966-2-1).</summary>
public static class Srgb
{
    public static double ToLinear(double v) => v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);

    public static double FromLinear(double v) => v <= 0.0031308 ? v * 12.92 : (1.055 * Math.Pow(v, 1 / 2.4)) - 0.055;
}
