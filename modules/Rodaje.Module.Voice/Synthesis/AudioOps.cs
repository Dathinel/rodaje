namespace Rodaje.Module.Voice.Synthesis;

/// <summary>Recorte de silencios en los bordes y remuestreo (para el tono).</summary>
internal static class AudioOps
{
    /// <summary>Quita el silencio de los bordes dejando <paramref name="marginSeconds"/>; el umbral es −40 dBFS.</summary>
    public static ReadOnlySpan<float> TrimSilence(float[] audio, int sampleRate, double marginSeconds = 0.03, float threshold = 0.01f)
    {
        int first = Array.FindIndex(audio, s => Math.Abs(s) > threshold);
        if (first < 0) return [];
        int last = Array.FindLastIndex(audio, s => Math.Abs(s) > threshold);
        int margin = (int)(marginSeconds * sampleRate);
        int from = Math.Max(0, first - margin);
        int to = Math.Min(audio.Length, last + 1 + margin);
        return audio.AsSpan(from, to - from);
    }

    /// <summary>
    /// Lee <paramref name="input"/> <paramref name="factor"/> veces más rápido (sube el tono y acorta) con interpolación
    /// sinc de 16 puntos con ventana de Hann, filtrada para no doblar frecuencias al acelerar.
    /// </summary>
    public static float[] Resample(ReadOnlySpan<float> input, double factor)
    {
        if (factor <= 0 || !double.IsFinite(factor)) throw new ArgumentOutOfRangeException(nameof(factor));
        if (Math.Abs(factor - 1) < 1e-6) return input.ToArray();
        const int half = 8;
        int length = (int)Math.Floor(input.Length / factor);
        float[] output = new float[length];
        double cutoff = Math.Min(1.0, 1.0 / factor);
        for (int i = 0; i < length; i++)
        {
            double pos = i * factor;
            int center = (int)Math.Floor(pos);
            double sum = 0, weight = 0;
            for (int k = center - half + 1; k <= center + half; k++)
            {
                if (k < 0 || k >= input.Length) continue;
                double x = pos - k;
                double w = 0.5 * (1 + Math.Cos(Math.PI * x / half));
                double s = cutoff * Sinc(cutoff * x) * w;
                sum += input[k] * s;
                weight += s;
            }
            output[i] = weight > 1e-9 ? (float)(sum / weight) : 0;
        }
        return output;
    }

    private static double Sinc(double x) => Math.Abs(x) < 1e-9 ? 1 : Math.Sin(Math.PI * x) / (Math.PI * x);
}
