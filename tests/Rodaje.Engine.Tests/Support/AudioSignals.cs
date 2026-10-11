using Rodaje.Core.Timing;

namespace Rodaje.Engine.Tests.Support;

/// <summary>Señales de prueba y medidas de audio: frecuencia, THD+N, saltos, RMS y correlación.</summary>
internal static class AudioSignals
{
    public const int Rate = 48_000;

    /// <summary>Ruido rosa por el método de Voss-McCartney simplificado de Paul Kellet, con semilla fija.</summary>
    public static float[] PinkNoise(int seed, int count)
    {
        var random = new Random(seed);
        var data = new float[count];
        double b0 = 0, b1 = 0, b2 = 0, b3 = 0, b4 = 0, b5 = 0, b6 = 0;
        for (int i = 0; i < count; i++)
        {
            double white = (random.NextDouble() * 2) - 1;
            b0 = (0.99886 * b0) + (white * 0.0555179);
            b1 = (0.99332 * b1) + (white * 0.0750759);
            b2 = (0.96900 * b2) + (white * 0.1538520);
            b3 = (0.86650 * b3) + (white * 0.3104856);
            b4 = (0.55000 * b4) + (white * 0.5329522);
            b5 = (-0.7616 * b5) - (white * 0.0168980);
            data[i] = (float)((b0 + b1 + b2 + b3 + b4 + b5 + b6 + (white * 0.5362)) * 0.05);
            b6 = white * 0.115926;
        }

        return data;
    }

    public static Func<long, float> Sine(double hz, double amplitude = 0.5) =>
        i => (float)(amplitude * Math.Sin(2 * Math.PI * hz * i / Rate));

    /// <summary>
    /// «Sílabas»: cada 200 ms, 150 ms de un tono armónico con su fundamental (120–250 Hz, con glissando) bajo una
    /// ventana de Hann y 50 ms de silencio. Se parece a la voz: periódica a ratos y con pausas.
    /// </summary>
    public static Func<long, float> Syllables(int seed)
    {
        const int Period = Rate / 5;
        const int Voiced = Rate * 3 / 20;
        var random = new Random(seed);
        double[] f0 = [.. Enumerable.Range(0, 4096).Select(_ => 120 + (random.NextDouble() * 130))];
        double[] glide = [.. Enumerable.Range(0, 4096).Select(_ => (random.NextDouble() - 0.5) * 60)];
        return i =>
        {
            if (i < 0)
            {
                return 0;
            }

            long syllable = i / Period;
            int at = (int)(i % Period);
            if (at >= Voiced)
            {
                return 0;
            }

            int n = (int)(syllable % f0.Length);
            double t = (double)at / Rate;
            double phase = 2 * Math.PI * ((f0[n] * t) + (glide[n] * t * t / 2 / 0.15));
            double sum = 0;
            for (int h = 1; h <= 5; h++)
            {
                sum += Math.Sin(h * phase) / h;
            }

            double envelope = 0.5 - (0.5 * Math.Cos(2 * Math.PI * at / Voiced));
            return (float)(0.3 * envelope * sum);
        };
    }

    public static float[] Left(ReadOnlySpan<float> interleaved)
    {
        var mono = new float[interleaved.Length / 2];
        for (int i = 0; i < mono.Length; i++)
        {
            mono[i] = interleaved[i * 2];
        }

        return mono;
    }

    /// <summary>Frecuencia por los cruces ascendentes por cero, interpolados.</summary>
    public static double Frequency(ReadOnlySpan<float> x)
    {
        double first = -1;
        double last = -1;
        int crossings = 0;
        for (int i = 1; i < x.Length; i++)
        {
            if (x[i - 1] < 0 && x[i] >= 0)
            {
                double at = i - 1 + (x[i - 1] / (double)(x[i - 1] - x[i]));
                if (first < 0)
                {
                    first = at;
                }

                last = at;
                crossings++;
            }
        }

        return crossings < 2 ? 0 : (crossings - 1) / ((last - first) / Rate);
    }

    /// <summary>
    /// THD+N en dB: en cada ventana ajusta un seno de <paramref name="hz"/> (amplitud, fase y continua libres) y
    /// compara lo que queda con lo ajustado. Como un analizador con filtro de muesca que sigue la fase.
    /// </summary>
    public static double ThdN(ReadOnlySpan<float> x, double hz, int window = 4096)
    {
        double residual = 0;
        double signal = 0;
        for (int start = 0; start + window <= x.Length; start += window)
        {
            (double r, double s) = Fit(x.Slice(start, window), hz, start);
            residual += r;
            signal += s;
        }

        return 10 * Math.Log10(residual / signal);
    }

    /// <summary>Amplitud del seno de <paramref name="hz"/> que mejor se ajusta a todo <paramref name="x"/>.</summary>
    public static double Amplitude(ReadOnlySpan<float> x, double hz)
    {
        (_, double s) = Fit(x, hz, 0);
        return Math.Sqrt(2 * s / x.Length);
    }

    public static double MaxStep(ReadOnlySpan<float> x)
    {
        double max = 0;
        for (int i = 1; i < x.Length; i++)
        {
            max = Math.Max(max, Math.Abs(x[i] - x[i - 1]));
        }

        return max;
    }

    public static double Rms(ReadOnlySpan<float> x)
    {
        double sum = 0;
        foreach (float v in x)
        {
            sum += v * (double)v;
        }

        return Math.Sqrt(sum / x.Length);
    }

    public static double Db(double ratio) => 20 * Math.Log10(ratio);

    public static double Correlation(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        double ab = 0, aa = 0, bb = 0;
        for (int i = 0; i < a.Length; i++)
        {
            ab += a[i] * (double)b[i];
            aa += a[i] * (double)a[i];
            bb += b[i] * (double)b[i];
        }

        return ab / Math.Sqrt(aa * bb);
    }

    private static (double Residual, double Signal) Fit(ReadOnlySpan<float> x, double hz, long offset)
    {
        // Mínimos cuadrados con base cos, sin y 1.
        double w = 2 * Math.PI * hz / Rate;
        double cc = 0, ss = 0, cs = 0, c1 = 0, s1 = 0, n = x.Length, xc = 0, xs = 0, x1 = 0;
        for (int i = 0; i < x.Length; i++)
        {
            double c = Math.Cos(w * (i + offset));
            double s = Math.Sin(w * (i + offset));
            cc += c * c;
            ss += s * s;
            cs += c * s;
            c1 += c;
            s1 += s;
            xc += x[i] * c;
            xs += x[i] * s;
            x1 += x[i];
        }

        double[,] m = { { cc, cs, c1 }, { cs, ss, s1 }, { c1, s1, n } };
        double[] v = [xc, xs, x1];
        double[] k = Solve3(m, v);
        double residual = 0;
        double signal = 0;
        for (int i = 0; i < x.Length; i++)
        {
            double fit = (k[0] * Math.Cos(w * (i + offset))) + (k[1] * Math.Sin(w * (i + offset)));
            double e = x[i] - fit - k[2];
            residual += e * e;
            signal += fit * fit;
        }

        return (residual, signal);
    }

    private static double[] Solve3(double[,] m, double[] v)
    {
        double det = Det(m);
        var result = new double[3];
        for (int c = 0; c < 3; c++)
        {
            var copy = (double[,])m.Clone();
            for (int r = 0; r < 3; r++)
            {
                copy[r, c] = v[r];
            }

            result[c] = Det(copy) / det;
        }

        return result;
    }

    private static double Det(double[,] m) =>
        (m[0, 0] * ((m[1, 1] * m[2, 2]) - (m[1, 2] * m[2, 1])))
        - (m[0, 1] * ((m[1, 0] * m[2, 2]) - (m[1, 2] * m[2, 0])))
        + (m[0, 2] * ((m[1, 0] * m[2, 1]) - (m[1, 1] * m[2, 0])));
}

/// <summary>Medio estéreo con la misma muestra en los dos canales, dada por su índice; cuenta los seeks.</summary>
internal sealed class FunctionPcm(Func<long, float> sample, long length) : IPcmSource
{
    private long _position;

    public int Seeks { get; private set; }

    public long Decoded { get; private set; }

    public int Read(Span<float> interleaved)
    {
        int frames = (int)Math.Min(interleaved.Length / 2, Math.Max(0, length - _position));
        for (int i = 0; i < frames; i++)
        {
            float v = sample(_position + i);
            interleaved[i * 2] = v;
            interleaved[(i * 2) + 1] = v;
        }

        _position += frames;
        Decoded += frames;
        return frames;
    }

    public void Seek(Time position)
    {
        _position = position.ToSamples(AudioSignals.Rate);
        Seeks++;
    }

    public void Dispose()
    {
    }
}
