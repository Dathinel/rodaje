using FFmpeg.AutoGen.Abstractions;
using Rodaje.Core.Timing;
using Rodaje.Media.Interop;
using Rodaje.Media.Probe;

namespace Rodaje.Media.Tests;

/// <summary>
/// Tarea 212: <see cref="Av.ToTime"/> y <see cref="Av.FromTime"/> pasaban el resultado a <see langword="long"/> sin
/// comprobarlo, así que una marca enorme (cabecera dañada) se truncaba a un instante inventado, y <c>AV_NOPTS_VALUE</c> se
/// convertía como si fuera una marca. Pruebas puras, sin FFmpeg.
/// </summary>
public sealed class AvTimeTests
{
    private static AVRational Base(int num, int den) => new() { num = num, den = den };

    public static TheoryData<int, int> TimeBases => new()
    {
        { 1, 1 },
        { 1, 1000 },
        { 1, 90_000 },
        { 1001, 30_000 },
        { 1, 1_000_000 },
        { 1, 1_000_000_000 },
    };

    [Fact]
    public void NoTimestamp_MatchesFfmpeg() => Assert.Equal(Av.NoTimestamp, ffmpeg.AV_NOPTS_VALUE);

    [Theory]
    [MemberData(nameof(TimeBases))]
    public void NoTimestamp_HasNoTime(int num, int den)
    {
        Assert.False(Av.TryToTime(Av.NoTimestamp, Base(num, den), out _));
        Assert.Throws<MediaException>(() => Av.ToTime(Av.NoTimestamp, Base(num, den)));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 1000)]
    [InlineData(1, 90_000)]
    [InlineData(1001, 30_000)]
    [InlineData(1, 1_000_000)]
    public void HugeTimestamp_DoesNotFit(int num, int den)
    {
        // A estas bases long.MaxValue pasa de los ~415 años que caben en Time; antes salía un instante truncado.
        Assert.False(Av.TryToTime(long.MaxValue, Base(num, den), out _));
        Assert.False(Av.TryToTime(long.MinValue + 1, Base(num, den), out _));
        Assert.Throws<MediaException>(() => Av.ToTime(long.MaxValue, Base(num, den)));
    }

    [Fact]
    public void JustPastTheLimit_DoesNotFit()
    {
        // En microsegundos (base del contenedor) el límite está en long.MaxValue / 705,6.
        long limit = (long)(((Int128)long.MaxValue * 1_000_000 - 500_000) / Time.TicksPerSecond);
        Assert.True(Av.TryToTime(limit, Base(1, 1_000_000), out Time last));
        Assert.True(last.Ticks > long.MaxValue - Time.TicksPerSecond);
        Assert.False(Av.TryToTime(limit + 1, Base(1, 1_000_000), out _));
    }

    [Fact]
    public void NanosecondBase_FitsEverything()
    {
        // Con 1/1 000 000 000 cualquier long cabe en Time (el tick es más grueso que el nanosegundo).
        Assert.True(Av.TryToTime(long.MaxValue, Base(1, 1_000_000_000), out Time max));
        Assert.Equal(Av.RoundDiv((Int128)long.MaxValue * Time.TicksPerSecond, 1_000_000_000), (Int128)max.Ticks);
        Assert.True(Av.TryToTime(long.MinValue + 1, Base(1, 1_000_000_000), out Time min));
        Assert.True(min.Ticks < 0);
    }

    [Theory]
    [MemberData(nameof(TimeBases))]
    public void OrdinaryTimestamps_RoundTrip(int num, int den)
    {
        foreach (long ts in new long[] { 0, 1, -1, 123_456_789, -987_654 })
        {
            Assert.True(Av.TryToTime(ts, Base(num, den), out Time time));
            Assert.Equal(ts, Av.FromTime(time, Base(num, den)));
        }
    }

    [Fact]
    public void FineBase_HugeTime_DoesNotFit()
    {
        // Time.MaxValue en nanosegundos no cabe en long (~415 años frente a ~292); antes se truncaba.
        Assert.False(Av.TryFromTime(Time.FromTicks(long.MaxValue), Base(1, 1_000_000_000), out _));
        Assert.False(Av.TryFromTime(Time.FromTicks(long.MinValue), Base(1, 1_000_000_000), out _));
        Assert.Throws<MediaException>(() => Av.FromTime(Time.FromTicks(long.MaxValue), Base(1, 1_000_000_000)));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 90_000)]
    [InlineData(1001, 30_000)]
    public void CoarseBase_ExtremeTimes_Fit(int num, int den)
    {
        Assert.True(Av.TryFromTime(Time.FromTicks(long.MaxValue), Base(num, den), out long max));
        Assert.True(max > 0);
        Assert.True(Av.TryFromTime(Time.FromTicks(long.MinValue), Base(num, den), out long min));
        Assert.True(min < 0 && min != Av.NoTimestamp);
    }

    [Fact]
    public void FromTime_NeverYieldsNoTimestamp()
    {
        // Un instante que cae justo en long.MinValue se leería como «sin marca».
        var tb = Base(1, 1_000_000_000);
        long ticks = (long)Av.RoundDiv((Int128)long.MinValue * Time.TicksPerSecond, 1_000_000_000);
        Assert.False(Av.TryFromTime(Time.FromTicks(ticks), tb, out _));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(0, 0)]
    public void InvalidBase_HasNoTime(int num, int den)
    {
        Assert.False(Av.TryToTime(1, Base(num, den), out _));
        Assert.False(Av.TryFromTime(Time.FromSeconds(1), Base(num, den), out _));
    }

    [Fact]
    public void ProbeDuration_TooLargeOrMissing_IsUnknown()
    {
        var microseconds = Base(1, 1_000_000);
        Assert.Null(MediaProbe.KnownDuration(long.MaxValue, microseconds));
        Assert.Null(MediaProbe.KnownDuration(Av.NoTimestamp, microseconds));
        Assert.Null(MediaProbe.KnownDuration(0, microseconds));
        Assert.Null(MediaProbe.KnownDuration(10, Base(1, 0)));
        Assert.Equal(Time.FromSeconds(2), MediaProbe.KnownDuration(2_000_000, microseconds));
        Assert.Equal(Time.FromSeconds(10), MediaProbe.KnownDuration(900_000, Base(1, 90_000)));
    }
}
