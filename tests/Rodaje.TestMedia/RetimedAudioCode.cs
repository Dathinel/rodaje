using System.Numerics;

namespace Rodaje.TestMedia;

/// <summary>
/// Las marcas de <see cref="AudioCode"/> oídas con velocidad: el chirp de cada segundo se busca con una plantilla que
/// suena como lo haría a esa velocidad. Con el tono mantenido el chirp conserva su forma (lo estira o lo acorta
/// repitiendo o saltando trozos); sin él se remuestrea y cambian a la vez su largo y sus frecuencias.
/// </summary>
/// <remarks>El número de segundo en FSK no se lee: quien busca sabe dónde debería estar cada marca y la busca ahí.</remarks>
public static class RetimedAudioCode
{
    private const int TemplateOversampling = 4;
    private const double ChirpSeconds = 0.020;

    /// <summary>El chirp tal como suena a <paramref name="rate"/>× (una muestra por cuadro de audio, mono).</summary>
    public static float[] ChirpTemplate(int sampleRate, double rate, bool preservePitch)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 8000);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rate);
        double step = preservePitch ? 1 : rate;
        int fine = sampleRate * TemplateOversampling;
        var template = new float[(int)Math.Ceiling(ChirpSeconds * sampleRate / step)];
        for (int i = 0; i < template.Length; i++)
        {
            double position = i * step * TemplateOversampling;
            long left = (long)Math.Floor(position);
            double t = position - left;
            template[i] = (float)(((1 - t) * AudioCode.SampleAt(left, fine)) + (t * AudioCode.SampleAt(left + 1, fine)));
        }

        return template;
    }

    /// <summary>Promedio de los canales de audio entrelazado.</summary>
    public static void Downmix(ReadOnlySpan<float> interleaved, int channels, Span<float> mono)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        if (mono.Length < interleaved.Length / channels)
        {
            throw new ArgumentException("El destino es más corto que el audio.", nameof(mono));
        }

        for (int i = 0; i < interleaved.Length / channels; i++)
        {
            float sum = 0;
            for (int c = 0; c < channels; c++)
            {
                sum += interleaved[(i * channels) + c];
            }

            mono[i] = sum / channels;
        }
    }

    /// <summary>
    /// Dónde empieza el chirp entre las muestras <paramref name="from"/> y <paramref name="to"/> (incluidas) de
    /// <paramref name="mono"/>: el desfase con mayor correlación normalizada; null si ninguno llega a
    /// <paramref name="minCorrelation"/>.
    /// </summary>
    public static AudioMark? FindChirp(ReadOnlySpan<float> mono, ReadOnlySpan<float> template, int from, int to, double minCorrelation = 0.6)
    {
        from = Math.Max(0, from);
        to = Math.Min(mono.Length - template.Length, to);
        double templateEnergy = Dot(template, template);
        long best = -1;
        double bestCorrelation = double.NegativeInfinity;
        for (int lag = from; lag <= to; lag++)
        {
            ReadOnlySpan<float> window = mono.Slice(lag, template.Length);
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

        return best >= 0 && bestCorrelation >= minCorrelation ? new AudioMark(best, null, bestCorrelation) : null;
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
}
