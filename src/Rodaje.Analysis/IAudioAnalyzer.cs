namespace Rodaje.Analysis;

/// <summary>
/// Consume audio float32 intercalado por bloques, en orden, sin guardarlo entero: una hora a 48 kHz son 690 MB por
/// canal, así que cada analizador reduce lo que recibe a una envolvente pequeña.
/// </summary>
public interface IAudioAnalyzer
{
    int SampleRate { get; }

    int Channels { get; }

    void Push(ReadOnlySpan<float> interleaved);
}

/// <summary>Reparte el mismo audio a varios analizadores, para decodificar una sola vez.</summary>
public sealed class AudioFanOut(params IAudioAnalyzer[] analyzers) : IAudioAnalyzer
{
    public int SampleRate => analyzers.Length > 0 ? analyzers[0].SampleRate : 0;

    public int Channels => analyzers.Length > 0 ? analyzers[0].Channels : 0;

    public void Push(ReadOnlySpan<float> interleaved)
    {
        foreach (IAudioAnalyzer analyzer in analyzers)
        {
            analyzer.Push(interleaved);
        }
    }
}

internal static class AnalyzerGuard
{
    public static void Check(int sampleRate, int channels)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 8000);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(sampleRate, 384_000);
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(channels, 32);
    }

    public static void CheckFrames(ReadOnlySpan<float> interleaved, int channels)
    {
        if (interleaved.Length % channels != 0)
        {
            throw new ArgumentException($"El bloque tiene {interleaved.Length} muestras, que no es múltiplo de {channels} canales.", nameof(interleaved));
        }
    }

    public static float Mono(ReadOnlySpan<float> interleaved, int frame, int channels)
    {
        if (channels == 1)
        {
            return interleaved[frame];
        }

        float sum = 0;
        int at = frame * channels;
        for (int c = 0; c < channels; c++)
        {
            sum += interleaved[at + c];
        }

        return sum / channels;
    }

    /// <summary>Percentil <paramref name="p"/> (0–1) de los valores mayores que <paramref name="floor"/>; NaN si no hay.</summary>
    public static double Percentile(IReadOnlyList<double> values, double p, double floor)
    {
        double[] kept = [.. values.Where(v => v > floor)];
        if (kept.Length == 0)
        {
            return double.NaN;
        }

        Array.Sort(kept);
        return kept[(int)Math.Clamp(Math.Round(p * (kept.Length - 1)), 0, kept.Length - 1)];
    }
}
