using FFmpeg.AutoGen.Abstractions;
using Rodaje.Core.Timing;
using Rodaje.Media.Audio;
using Rodaje.Media.Interop;

namespace Rodaje.Media.Tests;

/// <summary>
/// Tarea 219: los índices de cuadro (<c>VideoDecoder</c>, <c>KeyframeIndex</c>) y de muestra (<c>AudioDecoder</c>) se
/// calculaban con <c>(long)Av.RoundDiv((Int128)(marca − origen) · …)</c>: la resta envolvía en <see langword="long"/> y el
/// resultado se truncaba sin comprobar, así que una marca extrema de una cabecera dañada daba un índice inventado que
/// decidía qué cuadro se mostraba, qué keyframe se usaba o dónde se ponía el audio. Pruebas puras, sin FFmpeg.
/// </summary>
public sealed class FrameIndexOverflowTests
{
    private static AVRational Base(int num, int den) => new() { num = num, den = den };

    // La fórmula de antes, para mostrar a qué índice inventado llevaba cada caso.
    private static long OldSlot(long timestamp, long originTs, AVRational tb, Rational rate) =>
        (long)Av.RoundDiv((Int128)(timestamp - originTs) * tb.num * rate.Num, (Int128)tb.den * rate.Den);

    [Fact]
    public void HugeTimestamp_HasNoFrameIndex()
    {
        // long.MaxValue en segundos a 30 fps: antes salía el índice truncado.
        var tb = Base(1, 1);
        Assert.False(Av.TryFrameSlot(long.MaxValue, 0, tb, Rational.Fps30, out _));
        Assert.False(Av.TryFrameSlot(long.MinValue + 1, 0, tb, Rational.Fps30, out _));
        Assert.NotEqual((Int128)OldSlot(long.MaxValue, 0, tb, Rational.Fps30), (Int128)long.MaxValue * 30);
    }

    [Fact]
    public void WrappedSubtraction_GivesTheRealIndex()
    {
        // marca − origen no cabe en long: antes envolvía a −2 y el cuadro se tomaba por el 0.
        var tb = Base(1, 90_000);
        long origin = long.MinValue + 1;
        Assert.Equal(0, OldSlot(long.MaxValue, origin, tb, Rational.Fps30));
        Assert.True(Av.TryFrameSlot(long.MaxValue, origin, tb, Rational.Fps30, out long slot));
        Assert.Equal((long)Av.RoundDiv(((Int128)long.MaxValue - origin) * 30, 90_000), slot);
    }

    [Fact]
    public void IndexJustPastLong_HasNoFrameIndex()
    {
        // A 1 fps en base 1/1 el índice es marca − origen: con origen −1, long.MaxValue + 1 no cabe.
        var tb = Base(1, 1);
        var rate = new Rational(1, 1);
        Assert.Equal(long.MinValue, OldSlot(long.MaxValue, -1, tb, rate));
        Assert.False(Av.TryFrameSlot(long.MaxValue, -1, tb, rate, out _));
        Assert.True(Av.TryFrameSlot(long.MaxValue, 0, tb, rate, out long last));
        Assert.Equal(long.MaxValue, last);
    }

    [Fact]
    public void NoTimestampOrInvalidBase_HasNoFrameIndex()
    {
        Assert.False(Av.TryFrameSlot(Av.NoTimestamp, 0, Base(1, 90_000), Rational.Fps30, out _));
        Assert.False(Av.TryFrameSlot(1, 0, Base(0, 1), Rational.Fps30, out _));
        Assert.False(Av.TryFrameSlot(1, 0, Base(1, 0), Rational.Fps30, out _));
    }

    [Fact]
    public void HugeRate_DoesNotOverflowInt128()
    {
        // Numerador y denominador enormes: (marca − origen) · base · tasa no cabe ni en Int128.
        var rate = new Rational(long.MaxValue, long.MaxValue - 1);
        Assert.False(Av.TryFrameSlot(long.MaxValue, long.MinValue + 1, Base(int.MaxValue, 1), rate, out _));
    }

    [Theory]
    [InlineData(1, 1000, 0)]
    [InlineData(1, 90_000, 900)]
    [InlineData(1, 90_000, -3003)]
    [InlineData(1001, 30_000, 0)]
    [InlineData(1, 1_000_000_000, 5_000_000_000)]
    public void OrdinaryTimestamps_KeepTheirIndex(int num, int den, long origin)
    {
        var tb = Base(num, den);
        foreach (Rational rate in new[] { Rational.Fps24, Rational.Ntsc30, Rational.Fps60, new Rational(1000, 1) })
        {
            for (long frame = 0; frame < 200; frame += 7)
            {
                Assert.True(Av.TryFrameTimestamp(frame, origin, tb, rate, out long ts));
                Assert.True(Av.TryFrameSlot(ts, origin, tb, rate, out long slot));
                Assert.Equal(OldSlot(ts, origin, tb, rate), slot);
                if (rate.ToDouble() * num / den <= 1)
                {
                    // Con la base más fina que un cuadro, cada cuadro tiene su marca y vuelve a su índice.
                    Assert.Equal(frame, slot);
                }
            }
        }
    }

    [Fact]
    public void HugeFrameIndex_HasNoTimestamp()
    {
        // En nanosegundos el cuadro long.MaxValue a 30 fps cae a ~10^26: antes se truncaba a una marca inventada.
        Assert.False(Av.TryFrameTimestamp(long.MaxValue, 0, Base(1, 1_000_000_000), Rational.Fps30, out _));
        Assert.False(Av.TryFrameTimestamp(1, long.MaxValue, Base(1, 90_000), Rational.Fps30, out _));
        Assert.False(Av.TryFrameTimestamp(0, Av.NoTimestamp, Base(1, 90_000), Rational.Fps30, out _));
        Assert.True(Av.TryFrameTimestamp(0, long.MaxValue, Base(1, 90_000), Rational.Fps30, out long ts));
        Assert.Equal(long.MaxValue, ts);
    }

    [Fact]
    public void HugeAudioTimestamp_HasNoSampleIndex()
    {
        // Matroska (milisegundos) a 48 kHz: long.MaxValue · 48 no cabe; antes salía una muestra inventada.
        var ms = Base(1, 1000);
        Assert.False(AudioDecoder.TrySampleIndex(long.MaxValue, 0, ms, 48_000, out _));
        Assert.False(AudioDecoder.TrySampleIndex(long.MaxValue, long.MinValue + 1, Base(1, 90_000), 48_000, out _));
        Assert.False(AudioDecoder.TrySampleIndex(Av.NoTimestamp, 0, ms, 48_000, out _));
        Assert.False(AudioDecoder.TrySampleIndex(1000, 0, ms, 0, out _));
    }

    [Fact]
    public void SampleIndex_PastTheLimit_IsRejected()
    {
        // Cabe en long pero, multiplicado por la tasa de salida, ya no.
        var tb = Base(1, 48_000);
        Assert.True(AudioDecoder.TrySampleIndex(AudioDecoder.MaxSampleIndex, 0, tb, 48_000, out long max));
        Assert.Equal(AudioDecoder.MaxSampleIndex, max);
        Assert.False(AudioDecoder.TrySampleIndex(AudioDecoder.MaxSampleIndex + 1, 0, tb, 48_000, out _));
        Assert.False(AudioDecoder.TrySampleIndex(-AudioDecoder.MaxSampleIndex - 1, 0, tb, 48_000, out _));
    }

    [Theory]
    [InlineData(1, 1000, 44_100)]
    [InlineData(1, 48_000, 48_000)]
    [InlineData(1, 90_000, 22_050)]
    public void OrdinarySampleIndices_Unchanged(int num, int den, int sampleRate)
    {
        var tb = Base(num, den);
        foreach (long ts in new long[] { 0, 1, 1000, 123_456_789, -4096 })
        {
            Assert.True(AudioDecoder.TrySampleIndex(ts, 1000, tb, sampleRate, out long index));
            Assert.Equal((long)Av.RoundDiv((Int128)(ts - 1000) * num * sampleRate, den), index);
        }
    }
}
