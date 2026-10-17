namespace Rodaje.Analysis.Silence;

/// <summary>Ajustes de «Quitar silencios».</summary>
public sealed record SilenceOptions
{
    /// <summary>Umbral relativo al nivel de referencia (percentil 95 de los bloques con sonido), en dB.</summary>
    public double ThresholdDb { get; init; } = -35;

    /// <summary>Por debajo de este nivel absoluto todo es silencio, y el umbral nunca baja de él.</summary>
    public double AbsoluteFloorDb { get; init; } = -70;

    /// <summary>Lo que hay que subir sobre el umbral para salir del silencio (histéresis).</summary>
    public double HysteresisDb { get; init; } = 4;

    /// <summary>Silencio más corto que se corta, en segundos.</summary>
    public double MinimumSilence { get; init; } = 0.4;

    /// <summary>Un sonido más corto que esto dentro de un silencio (un clic) no lo interrumpe.</summary>
    public double MinimumSound { get; init; } = 0.03;

    /// <summary>Silencio que se deja tras el sonido anterior, en segundos.</summary>
    public double PaddingBefore { get; init; } = 0.1;

    /// <summary>Silencio que se deja antes del sonido siguiente, en segundos.</summary>
    public double PaddingAfter { get; init; } = 0.1;
}

/// <summary>Resultado: los silencios detectados y los tramos que se cortan (los silencios sin sus márgenes).</summary>
public sealed record SilenceAnalysis(
    IReadOnlyList<TimeRange> Silences,
    IReadOnlyList<TimeRange> Cuts,
    double ReferenceDb,
    double ThresholdDb,
    double Duration)
{
    public double CutSeconds => Cuts.Sum(c => c.Duration);
}

/// <summary>
/// Detección de silencios por energía: umbral relativo a la referencia con histéresis, clics absorbidos, duración mínima y
/// márgenes. Los bordes caen en bloques de la envolvente (5 ms).
/// </summary>
public static class SilenceDetector
{
    /// <summary>Obtiene las pausas como complemento de los tramos de voz devueltos por un detector de actividad vocal.</summary>
    public static SilenceAnalysis DetectFromVoice(IEnumerable<TimeRange> voice, double duration, SilenceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(voice);
        ArgumentOutOfRangeException.ThrowIfNegative(duration);
        options ??= new SilenceOptions();

        List<TimeRange> speech = TimeRange.Merge(voice
            .Where(r => r.End > 0 && r.Start < duration)
            .Select(r => new TimeRange(Math.Max(0, r.Start), Math.Min(duration, r.End))));
        var silences = new List<TimeRange>();
        double at = 0;
        foreach (TimeRange range in speech)
        {
            if (range.Start > at)
            {
                silences.Add(new(at, range.Start));
            }

            at = Math.Max(at, range.End);
        }

        if (at < duration)
        {
            silences.Add(new(at, duration));
        }

        List<TimeRange> kept = [.. silences.Where(s => s.Duration >= options.MinimumSilence)];
        var cuts = new List<TimeRange>(kept.Count);
        foreach (TimeRange silence in kept)
        {
            double start = silence.Start <= 0 ? 0 : silence.Start + options.PaddingBefore;
            double end = silence.End >= duration ? duration : silence.End - options.PaddingAfter;
            if (end > start)
            {
                cuts.Add(new(start, end));
            }
        }

        return new(kept, cuts, double.NaN, double.NaN, duration);
    }

    public static SilenceAnalysis Detect(LevelEnvelope envelope, SilenceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        options ??= new SilenceOptions();
        IReadOnlyList<double> levels = envelope.Levels;
        double block = envelope.BlockSeconds;
        double duration = envelope.Duration;

        double reference = AnalyzerGuard.Percentile(levels, 0.95, options.AbsoluteFloorDb);
        if (double.IsNaN(reference))
        {
            TimeRange all = new(0, duration);
            return new(duration > 0 ? [all] : [], duration >= options.MinimumSilence ? [all] : [], LevelEnvelope.FloorDb, options.AbsoluteFloorDb, duration);
        }

        double threshold = Math.Max(reference + options.ThresholdDb, options.AbsoluteFloorDb);
        double open = threshold + Math.Max(0, options.HysteresisDb);

        var silences = new List<TimeRange>();
        bool silent = levels.Count > 0 && levels[0] < threshold;
        int runStart = 0;
        for (int i = 1; i <= levels.Count; i++)
        {
            bool next = i < levels.Count && (silent ? levels[i] <= open : levels[i] < threshold);
            if (i < levels.Count && next == silent)
            {
                continue;
            }

            if (silent)
            {
                silences.Add(new(runStart * block, Math.Min(i * block, duration)));
            }

            silent = !silent;
            runStart = i;
        }

        // Un clic más corto que MinimumSound no separa dos silencios.
        List<TimeRange> joined = TimeRange.Merge(silences, options.MinimumSound);
        var kept = joined.Where(s => s.Duration >= options.MinimumSilence).ToList();

        var cuts = new List<TimeRange>(kept.Count);
        foreach (TimeRange s in kept)
        {
            double start = s.Start <= 0 ? 0 : s.Start + options.PaddingBefore;
            double end = s.End >= duration ? duration : s.End - options.PaddingAfter;
            if (end - start >= block)
            {
                cuts.Add(new(start, end));
            }
        }

        return new(kept, cuts, reference, threshold, duration);
    }
}
