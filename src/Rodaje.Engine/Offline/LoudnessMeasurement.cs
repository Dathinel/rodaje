namespace Rodaje.Engine.Offline;

/// <summary>Sonoridad de una mezcla según EBU R128 / ITU-R BS.1770.</summary>
/// <param name="IntegratedLufs">Sonoridad integrada, en LUFS; <see cref="double.NegativeInfinity"/> si es silencio.</param>
/// <param name="TruePeakDbtp">Pico verdadero, en dBTP.</param>
/// <param name="RangeLu">Rango de sonoridad (LRA), en LU.</param>
public readonly record struct LoudnessMeasurement(double IntegratedLufs, double TruePeakDbtp, double RangeLu)
{
    /// <summary>
    /// Ganancia en dB que lleva la mezcla a <paramref name="targetLufs"/> sin que el pico verdadero pase de
    /// <paramref name="maxTruePeakDbtp"/> (si haría falta más, se queda corta: no se limita). 0 en silencio.
    /// </summary>
    public double GainTo(double targetLufs, double maxTruePeakDbtp = -1)
    {
        if (!double.IsFinite(IntegratedLufs))
        {
            return 0;
        }

        double gain = targetLufs - IntegratedLufs;
        return double.IsFinite(TruePeakDbtp) ? Math.Min(gain, maxTruePeakDbtp - TruePeakDbtp) : gain;
    }
}
