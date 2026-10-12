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

    private KWeightingFilter _left;
    private KWeightingFilter _right;
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
            double l = _left.Next(interleaved[2 * i]);
            double r = _right.Next(interleaved[(2 * i) + 1]);
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
        double integrated = Loudness.Gated(blocks, -10);
        List<double> shortTerm = Windows(ShortTermSteps);
        var kept = new List<double>();
        _ = Loudness.Gated(shortTerm, -20, kept);
        double range = 0;
        if (kept.Count > 0)
        {
            kept.Sort();
            range = Percentile(kept, 0.95) - Percentile(kept, 0.10);
        }

        double peak = _peak > 0 ? 20 * Math.Log10(_peak) : double.NegativeInfinity;
        return new LoudnessMeasurement(integrated, peak, range);
    }

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

    private static double Percentile(List<double> sorted, double p)
    {
        double rank = p * (sorted.Count - 1);
        int low = (int)Math.Floor(rank);
        int high = Math.Min(sorted.Count - 1, low + 1);
        return sorted[low] + ((sorted[high] - sorted[low]) * (rank - low));
    }
}
