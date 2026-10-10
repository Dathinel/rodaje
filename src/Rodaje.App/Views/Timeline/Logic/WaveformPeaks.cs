using Rodaje.Core.Timing;

namespace Rodaje.App.Views.Timeline;

/// <summary>
/// Picos de la forma de onda de un medio: el valor absoluto máximo (0 a 1, todos los canales) de cada tramo de
/// <see cref="BucketDuration"/> del origen, desde el instante cero.
/// </summary>
public sealed class WaveformPeaks
{
    private readonly float[] _peaks;

    public WaveformPeaks(Time bucketDuration, float[] peaks)
    {
        ArgumentNullException.ThrowIfNull(peaks);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(bucketDuration.Ticks, 0L, nameof(bucketDuration));
        BucketDuration = bucketDuration;
        _peaks = peaks;
    }

    public Time BucketDuration { get; }

    public int Count => _peaks.Length;

    /// <summary>El pico más alto entre dos instantes del origen (al alejar, un píxel abarca varios tramos).</summary>
    public float Max(Time from, Time to)
    {
        if (_peaks.Length == 0)
        {
            return 0;
        }

        long first = Math.Clamp(from.Ticks / BucketDuration.Ticks, 0, _peaks.Length - 1);
        long last = Math.Clamp(Math.Max(from.Ticks, to.Ticks - 1) / BucketDuration.Ticks, first, _peaks.Length - 1);
        float max = 0;
        for (long i = first; i <= last; i++)
        {
            max = Math.Max(max, _peaks[i]);
        }

        return Math.Clamp(max, 0, 1);
    }
}
