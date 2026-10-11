using Rodaje.Audio.Metering;

namespace Rodaje.Engine.Offline;

/// <summary>
/// Sonoridad de una mezcla estéreo a 48 kHz según ITU-R BS.1770-4 y EBU R128: ponderación K, bloques de 400 ms con
/// solape del 75 % (pasos de 100 ms), puertas absoluta (−70 LUFS) y relativa (−10 LU); rango (LRA, EBU Tech 3342) con
/// ventanas de 3 s y puerta relativa de −20 LU; pico verdadero con <see cref="TruePeakDetector"/> (×4).
/// </summary>
internal sealed class LoudnessMeter
{
    public const int SampleRate = 48_000;

    private const int Step = SampleRate / 10;
    private const int BlockSteps = 4;
    private const int ShortTermSteps = 30;

    private readonly Biquad[] _shelf = [Shelf(), Shelf()];
    private readonly Biquad[] _highPass = [HighPass(), HighPass()];
    private readonly TruePeakDetector[] _peaks = [new(), new()];
    private readonly List<double> _steps = [];
    private double _stepEnergy;
    private int _stepFill;
    private float _peak;

    /// <summary>Procesa muestras estéreo intercaladas.</summary>
    public void Process(ReadOnlySpan<float> interleaved)
    {
        int frames = interleaved.Length / 2;
        for (int c = 0; c < 2; c++)
        {
            _peak = MathF.Max(_peak, _peaks[c].Process(interleaved, c, 2, frames));
        }

        for (int i = 0; i < frames; i++)
        {
            double l = _highPass[0].Next(_shelf[0].Next(interleaved[2 * i]));
            double r = _highPass[1].Next(_shelf[1].Next(interleaved[(2 * i) + 1]));
            _stepEnergy += (l * l) + (r * r);
            if (++_stepFill == Step)
            {
                _steps.Add(_stepEnergy / Step);
                _stepEnergy = 0;
                _stepFill = 0;
            }
        }
    }

    public LoudnessMeasurement Result()
    {
        List<double> blocks = Windows(BlockSteps);
        double integrated = Gated(blocks, -10, out _);
        List<double> shortTerm = Windows(ShortTermSteps);
        _ = Gated(shortTerm, -20, out List<double> kept);
        double range = 0;
        if (kept.Count > 0)
        {
            kept.Sort();
            range = Percentile(kept, 0.95) - Percentile(kept, 0.10);
        }

        double peak = _peak > 0 ? 20 * Math.Log10(_peak) : double.NegativeInfinity;
        return new LoudnessMeasurement(integrated, peak, range);
    }

    private static double Loudness(double energy) => -0.691 + (10 * Math.Log10(energy));

    /// <summary>Energía media de cada ventana de <paramref name="steps"/> pasos completos.</summary>
    private List<double> Windows(int steps)
    {
        var windows = new List<double>();
        double sum = 0;
        for (int i = 0; i < _steps.Count; i++)
        {
            sum += _steps[i];
            if (i >= steps)
            {
                sum -= _steps[i - steps];
            }

            if (i >= steps - 1)
            {
                windows.Add(Math.Max(0, sum) / steps);
            }
        }

        return windows;
    }

    /// <summary>Sonoridad de las ventanas que pasan la puerta absoluta y la relativa (<paramref name="relative"/> LU bajo su media).</summary>
    private static double Gated(List<double> energies, double relative, out List<double> kept)
    {
        kept = [];
        double absolute = Math.Pow(10, (-70 + 0.691) / 10);
        double sum = 0;
        int count = 0;
        foreach (double e in energies)
        {
            if (e > absolute)
            {
                sum += e;
                count++;
            }
        }

        if (count == 0)
        {
            return double.NegativeInfinity;
        }

        double gate = Math.Max(absolute, sum / count * Math.Pow(10, relative / 10));
        sum = 0;
        count = 0;
        foreach (double e in energies)
        {
            if (e > gate)
            {
                sum += e;
                count++;
                kept.Add(Loudness(e));
            }
        }

        return count == 0 ? double.NegativeInfinity : Loudness(sum / count);
    }

    private static double Percentile(List<double> sorted, double p)
    {
        double rank = p * (sorted.Count - 1);
        int low = (int)Math.Floor(rank);
        int high = Math.Min(sorted.Count - 1, low + 1);
        return sorted[low] + ((sorted[high] - sorted[low]) * (rank - low));
    }

    // Coeficientes de BS.1770-4 a 48 kHz.
    private static Biquad Shelf() => new(1.53512485958697, -2.69169618940638, 1.19839281085285, -1.69065929318241, 0.73248077421585);

    private static Biquad HighPass() => new(1.0, -2.0, 1.0, -1.99004745483398, 0.99007225036621);

    private sealed class Biquad(double b0, double b1, double b2, double a1, double a2)
    {
        private double _z1;
        private double _z2;

        public double Next(double x)
        {
            double y = (b0 * x) + _z1;
            _z1 = (b1 * x) - (a1 * y) + _z2;
            _z2 = (b2 * x) - (a2 * y);
            return y;
        }
    }
}
