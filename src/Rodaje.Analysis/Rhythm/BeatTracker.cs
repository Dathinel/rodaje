namespace Rodaje.Analysis.Rhythm;

/// <summary>Ajustes de la detección de ritmo.</summary>
public sealed record BeatOptions
{
    public double MinimumBpm { get; init; } = 60;

    public double MaximumBpm { get; init; } = 200;

    /// <summary>Tempo que se prefiere ante la duda entre el doble y la mitad.</summary>
    public double PreferredBpm { get; init; } = 120;

    /// <summary>Cuánto castiga el seguimiento apartarse del periodo estimado.</summary>
    public double Tightness { get; init; } = 100;
}

/// <summary>Pulsos (s desde el inicio del audio) y tempo estimado; sin pulsos, <see cref="Bpm"/> es 0.</summary>
public sealed record BeatAnalysis(IReadOnlyList<double> Beats, double Bpm, double Duration);

/// <summary>
/// Tempo por autocorrelación de la fuerza de ataque con preferencia logarítmica alrededor de 120 BPM, y pulsos por
/// programación dinámica (D. Ellis, «Beat Tracking by Dynamic Programming», 2007). El tempo final sale de la recta que
/// mejor ajusta los pulsos, que da décimas de BPM.
/// </summary>
public static class BeatTracker
{
    /// <summary>Retraso medido del flujo respecto del ataque real: los pulsos salían ~5,5 ms antes con clics y bombos sintéticos.</summary>
    internal const double LatencySeconds = -0.0055;

    public static BeatAnalysis Track(OnsetEnvelope envelope, BeatOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        options ??= new BeatOptions();
        double duration = (double)envelope.Flux.Count * envelope.Hop / envelope.AnalysisRate;
        double[] onset = Normalize(envelope.Flux);
        double fps = envelope.FrameRate;
        double period = EstimatePeriod(onset, fps, options);
        if (double.IsNaN(period))
        {
            return new([], 0, duration);
        }

        List<int> frames = TrackBeats(onset, period, options.Tightness);
        double[] beats = [.. frames.Select(f => Math.Max(0, envelope.FrameTime(f) - LatencySeconds))];
        return new(beats, beats.Length >= 4 ? 60 / Slope(beats) : 60 * fps / period, duration);
    }

    /// <summary>Quita la media local (±0,1 s) y deja solo lo positivo, con desviación típica 1.</summary>
    internal static double[] Normalize(IReadOnlyList<double> flux)
    {
        int n = flux.Count;
        var result = new double[n];
        const int half = 10;
        double sum = 0;
        int lo = 0, hi = -1;
        for (int i = 0; i < n; i++)
        {
            while (hi < Math.Min(n - 1, i + half))
            {
                sum += flux[++hi];
            }

            while (lo < i - half)
            {
                sum -= flux[lo++];
            }

            result[i] = Math.Max(0, flux[i] - (sum / (hi - lo + 1)));
        }

        double sq = result.Sum(v => v * v);
        double std = n > 0 ? Math.Sqrt(sq / n) : 0;
        if (std > 1e-9)
        {
            for (int i = 0; i < n; i++)
            {
                result[i] /= std;
            }
        }

        return result;
    }

    /// <summary>Periodo en cuadros (con decimales) o NaN si no hay ataques.</summary>
    internal static double EstimatePeriod(double[] onset, double fps, BeatOptions options)
    {
        int minLag = Math.Max(2, (int)Math.Floor(fps * 60 / options.MaximumBpm));
        int maxLag = (int)Math.Ceiling(fps * 60 / options.MinimumBpm);
        if (onset.Length < maxLag * 2 || onset.All(v => v <= 0))
        {
            return double.NaN;
        }

        // Con un periodo no entero los ataques caen a 37 o 38 cuadros y la autocorrelación se reparte entre dos retardos;
        // suavizar un poco el envolvente evita que el doble del periodo (entero) gane.
        onset = Smooth(onset, 48);
        double preferred = fps * 60 / options.PreferredBpm;
        var acf = new double[maxLag + 2];
        for (int lag = minLag - 1; lag <= maxLag + 1; lag++)
        {
            double sum = 0;
            for (int i = lag; i < onset.Length; i++)
            {
                sum += onset[i] * onset[i - lag];
            }

            acf[lag] = sum / (onset.Length - lag);
        }

        int best = -1;
        double bestScore = double.NegativeInfinity;
        for (int lag = minLag; lag <= maxLag; lag++)
        {
            double octaves = Math.Log2(lag / preferred);
            double score = acf[lag] * Math.Exp(-0.5 * octaves * octaves);
            if (acf[lag] >= acf[lag - 1] && acf[lag] >= acf[lag + 1] && score > bestScore)
            {
                bestScore = score;
                best = lag;
            }
        }

        if (best < 0 || acf[best] <= 0)
        {
            return double.NaN;
        }

        double a = acf[best - 1], b = acf[best], c = acf[best + 1];
        double denominator = a - (2 * b) + c;
        double shift = Math.Abs(denominator) > 1e-12 ? Math.Clamp(0.5 * (a - c) / denominator, -0.5, 0.5) : 0;
        return best + shift;
    }

    internal static List<int> TrackBeats(double[] onset, double period, double tightness)
    {
        int n = onset.Length;
        double[] local = Smooth(onset, period);
        var score = new double[n];
        var back = new int[n];
        int minStep = Math.Max(1, (int)Math.Round(period / 2));
        int maxStep = (int)Math.Round(period * 2);
        for (int i = 0; i < n; i++)
        {
            double best = 0;
            int from = -1;
            for (int step = minStep; step <= maxStep && step <= i; step++)
            {
                double penalty = Math.Log(step / period);
                double candidate = score[i - step] - (tightness * penalty * penalty);
                if (from < 0 || candidate > best)
                {
                    best = candidate;
                    from = i - step;
                }
            }

            score[i] = local[i] + (from >= 0 ? best : 0);
            back[i] = from;
        }

        // El último pulso: el último máximo local con puntaje al menos la mitad de la mediana de los máximos.
        var maxima = new List<int>();
        for (int i = 1; i < n - 1; i++)
        {
            if (score[i] > score[i - 1] && score[i] >= score[i + 1])
            {
                maxima.Add(i);
            }
        }

        if (maxima.Count == 0)
        {
            return [];
        }

        double[] values = [.. maxima.Select(i => score[i]).Order()];
        double median = values[values.Length / 2];
        int end = maxima.Last(i => score[i] >= 0.5 * median);

        var beats = new List<int>();
        for (int i = end; i >= 0; i = back[i])
        {
            beats.Add(i);
        }

        beats.Reverse();
        return Trim(beats, local);
    }

    /// <summary>Suaviza con una gaussiana de σ = periodo/32, como la puntuación local de Ellis.</summary>
    private static double[] Smooth(double[] onset, double period)
    {
        double sigma = Math.Max(0.5, period / 32);
        int radius = (int)Math.Ceiling(sigma * 3);
        var kernel = new double[(radius * 2) + 1];
        for (int k = -radius; k <= radius; k++)
        {
            kernel[k + radius] = Math.Exp(-0.5 * k * k / (sigma * sigma));
        }

        var result = new double[onset.Length];
        for (int i = 0; i < onset.Length; i++)
        {
            double sum = 0;
            for (int k = -radius; k <= radius; k++)
            {
                int j = i + k;
                if (j >= 0 && j < onset.Length)
                {
                    sum += onset[j] * kernel[k + radius];
                }
            }

            result[i] = sum;
        }

        return result;
    }

    /// <summary>Quita los pulsos débiles del principio y del final (donde todavía no hay música o ya terminó).</summary>
    private static List<int> Trim(List<int> beats, double[] local)
    {
        if (beats.Count == 0)
        {
            return beats;
        }

        double rms = Math.Sqrt(beats.Average(b => local[b] * local[b]));
        int first = beats.FindIndex(b => local[b] >= 0.5 * rms);
        int last = beats.FindLastIndex(b => local[b] >= 0.5 * rms);
        return first < 0 ? [] : beats.GetRange(first, last - first + 1);
    }

    /// <summary>Segundos por pulso: recta por mínimos cuadrados con cada pulso numerado según la mediana de los intervalos (un pulso perdido no tuerce el tempo).</summary>
    private static double Slope(double[] beats)
    {
        double[] intervals = [.. beats.Zip(beats.Skip(1), (a, b) => b - a).Order()];
        double median = intervals[intervals.Length / 2];
        var index = new double[beats.Length];
        for (int i = 1; i < beats.Length; i++)
        {
            index[i] = index[i - 1] + Math.Max(1, Math.Round((beats[i] - beats[i - 1]) / median));
        }

        double meanX = index.Average();
        double meanY = beats.Average();
        double num = 0, den = 0;
        for (int i = 0; i < beats.Length; i++)
        {
            num += (index[i] - meanX) * (beats[i] - meanY);
            den += (index[i] - meanX) * (index[i] - meanX);
        }

        return den > 0 ? num / den : median;
    }
}
