namespace Rodaje.Analysis.Tests;

/// <summary>Señales sintéticas con verdad conocida. Todas son deterministas (semilla fija).</summary>
internal static class Synth
{
    public const int Rate = 48_000;

    /// <summary>Estéreo intercalado a partir de mono.</summary>
    public static float[] Stereo(float[] mono)
    {
        var result = new float[mono.Length * 2];
        for (int i = 0; i < mono.Length; i++)
        {
            result[2 * i] = mono[i];
            result[(2 * i) + 1] = mono[i];
        }

        return result;
    }

    /// <summary>Habla simulada: ruido rosado aproximado más un tono armónico, modulados a ritmo de sílabas (4 Hz).</summary>
    public static void Speech(float[] buffer, double start, double end, double amplitude, Random random)
    {
        int a = (int)Math.Round(start * Rate), b = Math.Min(buffer.Length, (int)Math.Round(end * Rate));
        double pink = 0;
        for (int i = a; i < b; i++)
        {
            double t = (double)(i - a) / Rate;
            pink = (0.95 * pink) + (0.05 * ((random.NextDouble() * 2) - 1));
            double syllable = 0.55 + (0.45 * Math.Abs(Math.Sin(Math.PI * 4 * t)));
            buffer[i] += (float)(amplitude * syllable * ((3 * pink) + (0.4 * Voice(t))));
        }
    }

    /// <summary>Voz sintética: fundamental de 140 Hz con armónicos realzados en tres formantes (500, 1500, 2500 Hz).</summary>
    public static double Voice(double t)
    {
        double sum = 0;
        for (int h = 1; h <= 25; h++)
        {
            double f = 140 * h;
            double formant = Math.Exp(-Math.Pow((f - 500) / 200, 2)) + (0.6 * Math.Exp(-Math.Pow((f - 1500) / 300, 2))) + (0.4 * Math.Exp(-Math.Pow((f - 2500) / 400, 2)));
            sum += (0.1 + formant) * Math.Sin(2 * Math.PI * f * t) / h;
        }

        return sum;
    }

    /// <summary>Frase de voz sintética con ataque y caída de 10 ms y sílabas de 5 Hz.</summary>
    public static void Phrase(float[] buffer, double start, double end, double amplitude)
    {
        int a = (int)Math.Round(start * Rate), b = Math.Min(buffer.Length, (int)Math.Round(end * Rate));
        for (int i = a; i < b; i++)
        {
            double t = (double)(i - a) / Rate;
            double edge = Math.Min(1, Math.Min(t, (double)(b - i) / Rate) / 0.01);
            double syllable = 0.6 + (0.4 * Math.Abs(Math.Sin(Math.PI * 5 * t)));
            buffer[i] += (float)(amplitude * edge * syllable * Voice(t));
        }
    }

    /// <summary>Música de fondo: bajo de 55 Hz, acorde suave y bombo en cada pulso.</summary>
    public static void Music(float[] buffer, double amplitude, double bpm)
    {
        double beat = 60 / bpm;
        for (int i = 0; i < buffer.Length; i++)
        {
            double t = (double)i / Rate;
            double sinceBeat = t % beat;
            double kick = Math.Exp(-sinceBeat * 30) * Math.Sin(2 * Math.PI * (50 + (80 * Math.Exp(-sinceBeat * 40))) * sinceBeat);
            double pad = (0.3 * Math.Sin(2 * Math.PI * 55 * t)) + (0.15 * Math.Sin(2 * Math.PI * 220 * t)) + (0.1 * Math.Sin(2 * Math.PI * 277 * t));
            buffer[i] += (float)(amplitude * ((0.6 * kick) + pad));
        }
    }

    /// <summary>Clic de 2 ms (ruido con caída rápida) en cada instante.</summary>
    public static void Clicks(float[] buffer, IEnumerable<double> times, double amplitude, Random random)
    {
        foreach (double time in times)
        {
            int a = (int)Math.Round(time * Rate);
            for (int i = 0; i < Rate / 500 && a + i < buffer.Length; i++)
            {
                buffer[a + i] += (float)(amplitude * Math.Exp(-i / (Rate * 0.0005)) * ((random.NextDouble() * 2) - 1));
            }
        }
    }

    /// <summary>Patrón de batería: bombo en 1 y 3, caja en 2 y 4, platillo en corcheas.</summary>
    public static void Drums(float[] buffer, double bpm, double start, double amplitude, Random random)
    {
        double beat = 60 / bpm;
        int n = 0;
        for (double t = start; t < (double)buffer.Length / Rate; t += beat / 2, n++)
        {
            int a = (int)Math.Round(t * Rate);
            bool onBeat = n % 2 == 0;
            int beatIndex = n / 2;
            for (int i = 0; i < Rate / 5 && a + i < buffer.Length; i++)
            {
                double s = (double)i / Rate;
                double value = 0;
                if (onBeat && beatIndex % 2 == 0)
                {
                    value += Math.Exp(-s * 25) * Math.Sin(2 * Math.PI * (50 + (100 * Math.Exp(-s * 30))) * s);
                }
                else if (onBeat)
                {
                    value += 0.6 * Math.Exp(-s * 30) * (((random.NextDouble() * 2) - 1) + (0.5 * Math.Sin(2 * Math.PI * 190 * s)));
                }

                value += 0.15 * Math.Exp(-s * 120) * ((random.NextDouble() * 2) - 1);
                buffer[a + i] += (float)(amplitude * value);
            }
        }
    }

    public static void Noise(float[] buffer, double amplitude, Random random)
    {
        for (int i = 0; i < buffer.Length; i++)
        {
            buffer[i] += (float)(amplitude * ((random.NextDouble() * 2) - 1));
        }
    }

    public static float[] Seconds(double seconds) => new float[(int)Math.Round(seconds * Rate)];

    /// <summary>Pasa el audio al analizador en bloques de 1024 cuadros, como lo hace el decodificador.</summary>
    public static void Feed(IAudioAnalyzer analyzer, float[] interleaved)
    {
        int block = 1024 * analyzer.Channels;
        for (int i = 0; i < interleaved.Length; i += block)
        {
            analyzer.Push(interleaved.AsSpan(i, Math.Min(block, interleaved.Length - i)));
        }
    }

    /// <summary>F-measure con tolerancia: cada referencia empareja como mucho una detección.</summary>
    public static (double F, double MeanError) FMeasure(IReadOnlyList<double> detected, IReadOnlyList<double> truth, double tolerance)
    {
        var used = new bool[detected.Count];
        int hits = 0;
        double error = 0;
        foreach (double t in truth)
        {
            int best = -1;
            for (int i = 0; i < detected.Count; i++)
            {
                if (!used[i] && Math.Abs(detected[i] - t) <= tolerance && (best < 0 || Math.Abs(detected[i] - t) < Math.Abs(detected[best] - t)))
                {
                    best = i;
                }
            }

            if (best >= 0)
            {
                used[best] = true;
                hits++;
                error += detected[best] - t;
            }
        }

        double precision = detected.Count == 0 ? 0 : (double)hits / detected.Count;
        double recall = truth.Count == 0 ? 0 : (double)hits / truth.Count;
        double f = precision + recall == 0 ? 0 : 2 * precision * recall / (precision + recall);
        return (f, hits == 0 ? double.NaN : error / hits);
    }
}
