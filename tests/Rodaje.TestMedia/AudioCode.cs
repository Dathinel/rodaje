using System.Numerics;

namespace Rodaje.TestMedia;

/// <summary>Una marca encontrada: muestra donde empieza el chirp y número de segundo, si se pudo leer.</summary>
public readonly record struct AudioMark(long Sample, int? Second, double Correlation);

/// <summary>
/// Audio con marcas de tiempo. Cada segundo empieza con un chirp de 2 a 8 kHz de 20 ms, cuya
/// correlación con la plantilla da un pico de pocas muestras de ancho, y sigue con el número de
/// segundo módulo 1024 en FSK (11 bits de 40 ms con paridad par, 700 Hz = 0 y 1100 Hz = 1).
/// El resto del segundo queda en silencio. Todo sobrevive a AAC, Opus y al remuestreo.
/// </summary>
public static class AudioCode
{
    public const int SecondModulus = 1 << SecondBits;

    private const int SecondBits = 10;
    private const int TotalBits = SecondBits + 1;
    private const double ChirpSeconds = 0.020;
    private const double ChirpStartHz = 2000;
    private const double ChirpEndHz = 8000;
    private const double ChirpAmplitude = 0.5;
    private const double TaperSeconds = 0.002;
    private const double BitsStartSeconds = 0.060;
    private const double BitSeconds = 0.040;
    private const double ToneFadeSeconds = 0.003;
    private const double ZeroHz = 700;
    private const double OneHz = 1100;
    private const double ToneAmplitude = 0.25;
    private const double MinCorrelation = 0.6;

    /// <summary>Muestra mono en la posición absoluta <paramref name="sample"/>.</summary>
    public static float SampleAt(long sample, int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sample);
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 8000);

        long second = sample / sampleRate;
        double t = (double)(sample - second * sampleRate) / sampleRate;
        if (t < ChirpSeconds)
        {
            return (float)Chirp(t);
        }

        double bitsTime = t - BitsStartSeconds;
        int bit = (int)Math.Floor(bitsTime / BitSeconds);
        if (bitsTime < 0 || bit >= TotalBits)
        {
            return 0f;
        }

        double local = bitsTime - bit * BitSeconds;
        double frequency = BitOf((int)(second % SecondModulus), bit) ? OneHz : ZeroHz;
        double envelope = Taper(local, BitSeconds, ToneFadeSeconds);
        return (float)(ToneAmplitude * envelope * Math.Sin(2 * Math.PI * frequency * local));
    }

    /// <summary>
    /// Llena <paramref name="interleaved"/> con cuadros de audio entrelazados desde
    /// <paramref name="firstSample"/>; todos los canales llevan la misma señal.
    /// </summary>
    public static void Generate(Span<float> interleaved, long firstSample, int sampleRate, int channels)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        if (interleaved.Length % channels != 0)
        {
            throw new ArgumentException($"El búfer de {interleaved.Length} muestras no es múltiplo de {channels} canales.", nameof(interleaved));
        }

        int frames = interleaved.Length / channels;
        for (int i = 0; i < frames; i++)
        {
            float value = SampleAt(firstSample + i, sampleRate);
            interleaved.Slice(i * channels, channels).Fill(value);
        }
    }

    /// <summary>Busca las marcas en audio entrelazado; las posiciones son relativas al inicio del búfer.</summary>
    public static IReadOnlyList<AudioMark> Detect(ReadOnlySpan<float> interleaved, int sampleRate, int channels)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 8000);

        float[] mono = Downmix(interleaved, channels);
        float[] template = Template(sampleRate);
        int block = sampleRate / 1000;
        int blocks = mono.Length / block;
        var energy = new double[blocks];
        double maxEnergy = 0;
        for (int b = 0; b < blocks; b++)
        {
            double e = 0;
            int start = Math.Max(1, b * block);
            for (int i = start; i < (b + 1) * block; i++)
            {
                double d = mono[i] - mono[i - 1];
                e += d * d;
            }

            energy[b] = e;
            maxEnergy = Math.Max(maxEnergy, e);
        }

        var marks = new List<AudioMark>();
        if (maxEnergy <= 0)
        {
            return marks;
        }

        // Primera pasada barata: la diferencia entre muestras realza el chirp sobre los tonos FSK.
        double threshold = maxEnergy * 0.15;
        int minGapBlocks = (int)(0.3 * 1000);
        int lastHit = int.MinValue / 2;
        for (int b = 0; b < blocks; b++)
        {
            if (energy[b] < threshold || b - lastHit < minGapBlocks)
            {
                continue;
            }

            lastHit = b;
            int from = b * block - template.Length;
            int to = b * block + 2 * block;
            (long position, double correlation) = BestLag(mono, template, from, to);
            if (correlation < MinCorrelation)
            {
                continue;
            }

            marks.Add(new AudioMark(position, ReadSecond(mono, position, sampleRate), correlation));
        }

        return marks;
    }

    internal static bool BitOf(int second, int bit)
    {
        if (bit < SecondBits)
        {
            return ((second >> (SecondBits - 1 - bit)) & 1) != 0;
        }

        return (BitOperations.PopCount((uint)second) & 1) != 0;
    }

    private static double Chirp(double t)
    {
        double sweep = (ChirpEndHz - ChirpStartHz) / ChirpSeconds;
        double phase = 2 * Math.PI * (ChirpStartHz * t + sweep * t * t / 2);
        return ChirpAmplitude * Taper(t, ChirpSeconds, TaperSeconds) * Math.Sin(phase);
    }

    private static double Taper(double t, double length, double fade)
    {
        if (t < fade)
        {
            return 0.5 - 0.5 * Math.Cos(Math.PI * t / fade);
        }

        if (t > length - fade)
        {
            return 0.5 - 0.5 * Math.Cos(Math.PI * (length - t) / fade);
        }

        return 1;
    }

    private static float[] Template(int sampleRate)
    {
        var template = new float[(int)(ChirpSeconds * sampleRate)];
        for (int i = 0; i < template.Length; i++)
        {
            template[i] = (float)Chirp((double)i / sampleRate);
        }

        return template;
    }

    private static float[] Downmix(ReadOnlySpan<float> interleaved, int channels)
    {
        var mono = new float[interleaved.Length / channels];
        for (int i = 0; i < mono.Length; i++)
        {
            float sum = 0;
            for (int c = 0; c < channels; c++)
            {
                sum += interleaved[i * channels + c];
            }

            mono[i] = sum / channels;
        }

        return mono;
    }

    private static (long Position, double Correlation) BestLag(float[] signal, float[] template, int from, int to)
    {
        from = Math.Max(0, from);
        to = Math.Min(signal.Length - template.Length, to);
        double templateEnergy = Dot(template, template);
        long best = -1;
        double bestCorrelation = double.NegativeInfinity;
        for (int lag = from; lag <= to; lag++)
        {
            ReadOnlySpan<float> window = signal.AsSpan(lag, template.Length);
            double windowEnergy = Dot(window, window);
            if (windowEnergy <= 0)
            {
                continue;
            }

            double correlation = Dot(window, template) / Math.Sqrt(windowEnergy * templateEnergy);
            if (correlation > bestCorrelation)
            {
                bestCorrelation = correlation;
                best = lag;
            }
        }

        return (best, bestCorrelation);
    }

    private static double Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var acc = Vector<float>.Zero;
        int i = 0;
        for (; i <= a.Length - Vector<float>.Count; i += Vector<float>.Count)
        {
            acc += new Vector<float>(a[i..]) * new Vector<float>(b[i..]);
        }

        double sum = Vector.Sum(acc);
        for (; i < a.Length; i++)
        {
            sum += a[i] * b[i];
        }

        return sum;
    }

    private static int? ReadSecond(float[] mono, long markStart, int sampleRate)
    {
        int second = 0;
        bool parity = false;
        for (int bit = 0; bit < TotalBits; bit++)
        {
            // Solo el centro de cada bit: evita los bordes y el corrimiento del códec.
            long start = markStart + (long)((BitsStartSeconds + bit * BitSeconds + 0.005) * sampleRate);
            int length = (int)((BitSeconds - 0.010) * sampleRate);
            if (start + length > mono.Length)
            {
                return null;
            }

            ReadOnlySpan<float> window = mono.AsSpan((int)start, length);
            double zero = Goertzel(window, ZeroHz, sampleRate);
            double one = Goertzel(window, OneHz, sampleRate);
            if (Math.Max(zero, one) < 4 * Math.Min(zero, one))
            {
                return null;
            }

            bool value = one > zero;
            if (bit < SecondBits)
            {
                second = (second << 1) | (value ? 1 : 0);
            }
            else
            {
                parity = value;
            }
        }

        return parity == ((BitOperations.PopCount((uint)second) & 1) != 0) ? second : null;
    }

    private static double Goertzel(ReadOnlySpan<float> samples, double frequency, int sampleRate)
    {
        double coefficient = 2 * Math.Cos(2 * Math.PI * frequency / sampleRate);
        double s1 = 0;
        double s2 = 0;
        foreach (float x in samples)
        {
            double s0 = x + coefficient * s1 - s2;
            s2 = s1;
            s1 = s0;
        }

        return s1 * s1 + s2 * s2 - coefficient * s1 * s2;
    }
}
