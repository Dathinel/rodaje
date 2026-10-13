using Rodaje.Core.Timing;
using Rodaje.Media.Probe;

namespace Rodaje.Media.Tests;

/// <summary>
/// Un contenedor dañado puede declarar más cuadros de los que caben en <see cref="Time"/>: <see cref="Time.FromFrames"/>
/// lanzaba <see cref="OverflowException"/> y quien lee el medio solo espera <see cref="MediaException"/> (tarea 94).
/// </summary>
public sealed class MediaProbeOverflowTests
{
    [Theory]
    [InlineData(long.MaxValue, 30, 1)]
    [InlineData(long.MaxValue, 1, 1)]
    [InlineData(long.MaxValue, 30000, 1001)]
    [InlineData(1L << 40, 1, 1)]
    [InlineData(1L << 50, 1000, 1)]
    public void HugeFrameCounts_FitInTime(long frames, long num, long den)
    {
        var rate = new Rational(num, den);
        long safe = MediaProbe.SafeFrameCount(frames, rate);

        Assert.InRange(safe, 1, frames);
        Time duration = Time.FromFrames(safe, rate);
        Assert.True(duration > Time.Zero);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(1_000_000L)]
    public void NormalFrameCounts_AreKept(long frames) =>
        Assert.Equal(frames, MediaProbe.SafeFrameCount(frames, Rational.Fps30));
}
