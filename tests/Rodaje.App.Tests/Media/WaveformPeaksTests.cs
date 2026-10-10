using Rodaje.App.Services.Media;

namespace Rodaje.App.Tests.Media;

public sealed class WaveformPeaksTests
{
    private const int Rate = 48_000;

    /// <summary>Estéreo: izquierda una rampa de −1 a 1 por segundo, derecha la mitad con el signo cambiado.</summary>
    private static float[] Ramp(int seconds)
    {
        var samples = new float[seconds * Rate * 2];
        for (int i = 0; i < seconds * Rate; i++)
        {
            float v = ((i % Rate) / (float)(Rate - 1) * 2) - 1;
            samples[2 * i] = v;
            samples[(2 * i) + 1] = -v / 2;
        }

        return samples;
    }

    private static WaveformPeaks Build(float[] interleaved, int chunk = 7_919)
    {
        var peaks = new WaveformPeaks(Rate);
        for (int offset = 0; offset < interleaved.Length; offset += chunk * 2)
        {
            peaks.Append(interleaved.AsSpan(offset, Math.Min(chunk * 2, interleaved.Length - offset)), 2);
        }

        peaks.Complete();
        return peaks;
    }

    [Fact]
    public void Each_base_peak_is_min_and_max_over_all_channels()
    {
        float[] samples = new float[WaveformPeaks.BaseSamplesPerPeak * 2 * 2];
        samples[10] = 0.5f;
        samples[11] = -0.25f;
        samples[(WaveformPeaks.BaseSamplesPerPeak * 2) + 3] = -1f;
        WaveformPeaks peaks = Build(samples);

        (float[] min, float[] max) = peaks.Query(0, WaveformPeaks.BaseSamplesPerPeak * 2, 2);

        Assert.Equal(2, peaks.Count);
        Assert.Equal(-0.25f, min[0], 3);
        Assert.Equal(0.5f, max[0], 3);
        Assert.Equal(-1f, min[1], 3);
        Assert.Equal(0f, max[1], 3);
    }

    [Fact]
    public void One_bucket_per_second_of_a_ramp_spans_the_full_range()
    {
        WaveformPeaks peaks = Build(Ramp(4));

        (float[] min, float[] max) = peaks.Query(0, 4 * Rate, 4);

        Assert.Equal(4 * Rate, peaks.Samples);
        Assert.All(min, v => Assert.Equal(-1f, v, 3));
        Assert.All(max, v => Assert.Equal(1f, v, 3));
    }

    [Fact]
    public void Coarse_levels_agree_with_aggregating_the_base_level()
    {
        var random = new Random(3);
        float[] noise = new float[30 * Rate * 2];
        for (int i = 0; i < noise.Length; i++)
        {
            noise[i] = (float)((random.NextDouble() * 2) - 1) * (float)Math.Sin(i / 90_000.0);
        }

        WaveformPeaks peaks = Build(noise);
        var partial = new WaveformPeaks(Rate);
        partial.Append(noise, 2);

        // Los completos usan un nivel grueso; los parciales (sin cerrar) solo el de base. Con tramos alineados a las
        // celdas del nivel grueso dan lo mismo; sin alinear, el grueso envuelve al fino (nunca recorta un pico).
        const int Cell = WaveformPeaks.BaseSamplesPerPeak * 64;
        (float[] alignedMin, float[] alignedMax) = peaks.Query(0, Cell * 80, 80);
        (float[] baseAlignedMin, float[] baseAlignedMax) = partial.Query(0, Cell * 80, 80);
        (float[] coarseMin, float[] coarseMax) = peaks.Query(0, 30 * Rate, 37);
        (float[] baseMin, float[] baseMax) = partial.Query(0, 30 * Rate, 37);

        Assert.False(partial.IsComplete);
        Assert.Equal(baseAlignedMin, alignedMin);
        Assert.Equal(baseAlignedMax, alignedMax);
        for (int i = 0; i < 37; i++)
        {
            Assert.True(coarseMin[i] <= baseMin[i] && coarseMax[i] >= baseMax[i]);
        }
    }

    [Fact]
    public void Ranges_beyond_the_audio_are_silent_and_empty_ranges_are_zero()
    {
        WaveformPeaks peaks = Build(Ramp(1));

        (float[] min, float[] max) = peaks.Query(2 * Rate, 3 * Rate, 5);
        (float[] none, _) = peaks.Query(Rate, Rate, 4);

        Assert.All(min, v => Assert.Equal(0, v));
        Assert.All(max, v => Assert.Equal(0, v));
        Assert.All(none, v => Assert.Equal(0, v));
        Assert.Empty(peaks.Query(0, Rate, 0).Min);
    }

    [Fact]
    public void Very_zoomed_in_buckets_repeat_the_base_peak()
    {
        WaveformPeaks peaks = Build(Ramp(1));

        (float[] min, float[] max) = peaks.Query(0, WaveformPeaks.BaseSamplesPerPeak, 8);

        Assert.All(min, v => Assert.Equal(min[0], v));
        Assert.All(max, v => Assert.Equal(max[0], v));
    }

    [Fact]
    public void Partial_peaks_can_be_read_while_they_grow()
    {
        var peaks = new WaveformPeaks(Rate);
        float[] second = Ramp(1);
        peaks.Append(second, 2);
        int first = peaks.Count;
        (float[] _, float[] early) = peaks.Query(0, 2 * Rate, 2);
        peaks.Append(second, 2);
        (float[] _, float[] later) = peaks.Query(0, 2 * Rate, 2);

        Assert.Equal(Rate / WaveformPeaks.BaseSamplesPerPeak, first);
        Assert.Equal(1f, early[0], 1);
        Assert.Equal(0f, early[1]);
        Assert.Equal(1f, later[1], 1);
    }

    [Fact]
    public void Serializes_and_restores_the_same_answers()
    {
        WaveformPeaks peaks = Build(Ramp(3));

        WaveformPeaks restored = WaveformPeaks.Deserialize(peaks.Serialize())!;

        Assert.True(restored.IsComplete);
        Assert.Equal(peaks.Count, restored.Count);
        Assert.Equal(peaks.Samples, restored.Samples);
        Assert.Equal(peaks.Query(1000, 2.5 * Rate, 50).Min, restored.Query(1000, 2.5 * Rate, 50).Min);
        Assert.Equal(peaks.Query(1000, 2.5 * Rate, 50).Max, restored.Query(1000, 2.5 * Rate, 50).Max);
    }

    [Fact]
    public void Corrupt_data_is_rejected()
    {
        byte[] good = Build(Ramp(1)).Serialize();
        byte[] truncated = good[..^2];
        byte[] badMagic = [.. good];
        badMagic[0] ^= 0xFF;

        Assert.Null(WaveformPeaks.Deserialize(truncated));
        Assert.Null(WaveformPeaks.Deserialize(badMagic));
        Assert.Null(WaveformPeaks.Deserialize([]));
        Assert.Throws<InvalidOperationException>(() => new WaveformPeaks(Rate).Serialize());
    }

    [Fact]
    public void An_absurd_duration_does_not_reserve_gigabytes()
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        var peaks = new WaveformPeaks(Rate, long.MaxValue / 2);
        long reserved = GC.GetAllocatedBytesForCurrentThread() - before;

        peaks.Append(new float[Rate * 2], 2);
        peaks.Complete();

        Assert.InRange(reserved, 0, 32L << 20);
        Assert.InRange(peaks.Count, Rate / WaveformPeaks.BaseSamplesPerPeak, (Rate / WaveformPeaks.BaseSamplesPerPeak) + 1);
    }
}
