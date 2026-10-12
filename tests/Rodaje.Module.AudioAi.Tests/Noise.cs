namespace Rodaje.Module.AudioAi.Tests;

/// <summary>Ruido sintético determinista y medidas de calidad sobre una referencia limpia conocida.</summary>
internal static class Noise
{
    public enum Kind
    {
        /// <summary>Gaussiano blanco (ventilador, preamplificador).</summary>
        White,

        /// <summary>Rosa (−3 dB/octava; ambiente, lluvia).</summary>
        Pink,

        /// <summary>Zumbido de red de 50 Hz con armónicos más ruido grave (aire acondicionado).</summary>
        Hum,
    }

    public static float[] Make(Kind kind, int length, int sampleRate, int seed)
    {
        var random = new Random(seed);
        var noise = new float[length];
        double b0 = 0, b1 = 0, b2 = 0, b3 = 0, b4 = 0, b5 = 0, b6 = 0, low = 0;
        for (int i = 0; i < length; i++)
        {
            double white = Gaussian(random);
            switch (kind)
            {
                case Kind.White:
                    noise[i] = (float)white;
                    break;
                case Kind.Pink:
                    // Filtro de Paul Kellet.
                    b0 = (0.99886 * b0) + (white * 0.0555179);
                    b1 = (0.99332 * b1) + (white * 0.0750759);
                    b2 = (0.96900 * b2) + (white * 0.1538520);
                    b3 = (0.86650 * b3) + (white * 0.3104856);
                    b4 = (0.55000 * b4) + (white * 0.5329522);
                    b5 = (-0.7616 * b5) - (white * 0.0168980);
                    noise[i] = (float)(b0 + b1 + b2 + b3 + b4 + b5 + b6 + (white * 0.5362));
                    b6 = white * 0.115926;
                    break;
                default:
                    double t = (double)i / sampleRate;
                    double hum = 0;
                    for (int h = 1; h <= 7; h++)
                    {
                        hum += Math.Sin((2 * Math.PI * 50 * h * t) + h) / h;
                    }

                    low = (0.98 * low) + (0.02 * white);
                    noise[i] = (float)(hum + (8 * low));
                    break;
            }
        }

        return noise;
    }

    /// <summary><paramref name="clean"/> más <paramref name="noise"/> escalado para quedar a <paramref name="snrDb"/>.</summary>
    public static float[] Mix(float[] clean, float[] noise, double snrDb)
    {
        double scale = Math.Sqrt(Energy(clean) / Energy(noise) / Math.Pow(10, snrDb / 10));
        var mixed = new float[clean.Length];
        for (int i = 0; i < clean.Length; i++)
        {
            mixed[i] = clean[i] + (float)(scale * noise[i]);
        }

        return mixed;
    }

    public static double Snr(float[] reference, float[] estimate)
    {
        double error = 0;
        for (int i = 0; i < reference.Length; i++)
        {
            double d = reference[i] - estimate[i];
            error += d * d;
        }

        return 10 * Math.Log10(Energy(reference) / Math.Max(error, 1e-20));
    }

    /// <summary>SNR invariante a la escala (SI-SDR): no castiga una ganancia global distinta.</summary>
    public static double SiSnr(float[] reference, float[] estimate)
    {
        double dot = 0;
        for (int i = 0; i < reference.Length; i++)
        {
            dot += reference[i] * (double)estimate[i];
        }

        double alpha = dot / Energy(reference);
        double target = 0, error = 0;
        for (int i = 0; i < reference.Length; i++)
        {
            double s = alpha * reference[i];
            double e = estimate[i] - s;
            target += s * s;
            error += e * e;
        }

        return 10 * Math.Log10(target / Math.Max(error, 1e-20));
    }

    public static double Energy(float[] x)
    {
        double sum = 0;
        foreach (float v in x)
        {
            sum += v * (double)v;
        }

        return sum;
    }

    private static double Gaussian(Random random) =>
        Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble());
}
