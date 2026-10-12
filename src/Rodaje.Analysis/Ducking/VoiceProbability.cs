namespace Rodaje.Analysis.Ducking;

/// <summary>Ajustes para pasar de probabilidad de voz por ventana (un detector con modelo, como Silero) a tramos.</summary>
public sealed record VoiceProbabilityOptions
{
    /// <summary>Desde esta probabilidad empieza la voz.</summary>
    public double Threshold { get; init; } = 0.5;

    /// <summary>La voz sigue hasta bajar de aquí (histéresis); por omisión, <see cref="Threshold"/> − 0,15.</summary>
    public double? EndThreshold { get; init; }

    /// <summary>Pausas más cortas que esto no cortan la voz.</summary>
    public double MinimumSilence { get; init; } = 0.1;

    /// <summary>Tramos más cortos que esto se descartan.</summary>
    public double MinimumSpeech { get; init; } = 0.25;

    /// <summary>Margen que se suma a cada lado de un tramo (sin pisar al vecino).</summary>
    public double Padding { get; init; } = 0.03;
}

/// <summary>
/// Probabilidades por ventana a tramos de voz, con el mismo criterio que <c>get_speech_timestamps</c> de Silero VAD (MIT):
/// umbral con histéresis, silencio mínimo para cortar, voz mínima y margen repartido entre tramos vecinos.
/// </summary>
public static class VoiceProbability
{
    /// <param name="probabilities">Una por ventana, en orden.</param>
    /// <param name="windowSeconds">Largo de cada ventana (32 ms en Silero a 16 kHz).</param>
    /// <param name="duration">Largo del audio analizado, en segundos.</param>
    /// <param name="options">Umbrales y duraciones; por omisión los de Silero.</param>
    public static List<TimeRange> ToRanges(ReadOnlySpan<float> probabilities, double windowSeconds, double duration, VoiceProbabilityOptions? options = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(windowSeconds);
        options ??= new VoiceProbabilityOptions();
        double endThreshold = options.EndThreshold ?? (options.Threshold - 0.15);
        var ranges = new List<TimeRange>();
        bool speaking = false;
        double start = 0, silenceFrom = -1;
        for (int i = 0; i < probabilities.Length; i++)
        {
            double t = i * windowSeconds;
            float p = probabilities[i];
            if (p >= options.Threshold)
            {
                silenceFrom = -1;
                if (!speaking)
                {
                    speaking = true;
                    start = t;
                }

                continue;
            }

            if (speaking && p < endThreshold)
            {
                if (silenceFrom < 0)
                {
                    silenceFrom = t;
                }

                if (t - silenceFrom >= options.MinimumSilence)
                {
                    if (silenceFrom - start > options.MinimumSpeech)
                    {
                        ranges.Add(new TimeRange(start, silenceFrom));
                    }

                    speaking = false;
                    silenceFrom = -1;
                }
            }
        }

        if (speaking && duration - start > options.MinimumSpeech)
        {
            ranges.Add(new TimeRange(start, duration));
        }

        return Pad(ranges, options.Padding, duration);
    }

    private static List<TimeRange> Pad(List<TimeRange> ranges, double padding, double duration)
    {
        TimeRange[] raw = [.. ranges];
        for (int i = 0; i < raw.Length; i++)
        {
            double before = i == 0 ? padding : Math.Min(padding, (raw[i].Start - raw[i - 1].End) / 2);
            double after = i == raw.Length - 1 ? padding : Math.Min(padding, (raw[i + 1].Start - raw[i].End) / 2);
            ranges[i] = new TimeRange(Math.Max(0, raw[i].Start - before), Math.Min(duration, raw[i].End + after));
        }

        return ranges;
    }
}
