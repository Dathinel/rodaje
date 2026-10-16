using System;
using Rodaje.Core.Timing;
using Xunit;

namespace Rodaje.Core.Tests.Caza;

/// <summary>
/// Lo que promete «Tiempo exacto (<c>Timing/</c>)» de <c>docs/arquitectura/nucleo.md</c>. Cada prueba cita la frase.
/// </summary>
public sealed class TimingSectionContractTests
{
    public static TheoryData<long, long> CommonRates => new()
    {
        { 24, 1 }, { 25, 1 }, { 30, 1 }, { 50, 1 }, { 60, 1 },
        { 24000, 1001 }, { 30000, 1001 }, { 60000, 1001 },
    };

    // «un segundo tiene **705 600 000 ticks**»
    [Fact]
    public void OneSecond_Is705600000Ticks()
    {
        Assert.Equal(705_600_000, Time.TicksPerSecond);
        Assert.Equal(Time.TicksPerSecond, Time.FromSeconds(1).Ticks);
        Assert.Equal(1.0, Time.FromTicks(705_600_000).ToSeconds());
    }

    // «Ese número es divisible entre la duración de un cuadro a 24, 25, 30, 50, 60 fps y sus variantes NTSC»
    [Theory]
    [MemberData(nameof(CommonRates))]
    public void FrameDuration_IsWholeTicks_AtCommonRates(long num, long den)
    {
        var rate = new Rational(num, den);
        Assert.Equal(0, Time.TicksPerSecond * rate.Den % rate.Num);

        long perFrame = Time.TicksPerSecond * rate.Den / rate.Num;
        Assert.Equal(perFrame, Time.FromFrames(1, rate).Ticks);
        Assert.Equal(perFrame * 1000, Time.FromFrames(1000, rate).Ticks);
    }

    // «(29,97 fps = 23 543 520 ticks por cuadro)»
    [Fact]
    public void Ntsc30_FrameIs23543520Ticks()
    {
        Assert.Equal(23_543_520, Time.FromFrames(1, Rational.Ntsc30).Ticks);
    }

    // «y entre la de una muestra a 44,1 kHz (16 000 ticks) y 48 kHz (14 700 ticks)»
    [Fact]
    public void SampleDuration_IsWholeTicks_At44100And48000()
    {
        Assert.Equal(16_000, Time.FromSamples(1, 44_100).Ticks);
        Assert.Equal(14_700, Time.FromSamples(1, 48_000).Ticks);
        Assert.Equal(Time.TicksPerSecond, Time.FromSamples(44_100, 44_100).Ticks);
        Assert.Equal(Time.TicksPerSecond, Time.FromSamples(48_000, 48_000).Ticks);
    }

    // «así que sumar y restar posiciones es aritmética entera sin deriva»
    [Theory]
    [MemberData(nameof(CommonRates))]
    public void AddingFramesOneByOne_HasNoDrift(long num, long den)
    {
        var rate = new Rational(num, den);
        Time frame = Time.FromFrames(1, rate);
        Time sum = Time.Zero;
        for (int i = 0; i < 100_000; i++)
        {
            sum += frame;
        }

        Assert.Equal(Time.FromFrames(100_000, rate), sum);
        Assert.Equal(100_000, sum.ToFrames(rate));

        for (int i = 0; i < 100_000; i++)
        {
            sum -= frame;
        }

        Assert.Equal(Time.Zero, sum);
    }

    // «un `long` alcanza para más de 400 años»
    [Fact]
    public void Long_CoversMoreThan400Years()
    {
        const long secondsIn400Years = 400L * 366 * 24 * 3600;
        Assert.True(long.MaxValue / Time.TicksPerSecond > secondsIn400Years);
        Assert.Equal(secondsIn400Years * Time.TicksPerSecond, Time.FromSeconds(secondsIn400Years).Ticks);
    }

    // «Los operadores usan `checked`: se lanza `OverflowException` en vez de desbordarse en silencio.»
    [Fact]
    public void Operators_ThrowOverflowException()
    {
        Time max = Time.FromTicks(long.MaxValue);
        Time min = Time.FromTicks(long.MinValue);
        Time one = Time.FromTicks(1);

        Assert.Throws<OverflowException>(() => max + one);
        Assert.Throws<OverflowException>(() => min - one);
        Assert.Throws<OverflowException>(() => -min);
        Assert.Throws<OverflowException>(() => max.Add(one));
        Assert.Throws<OverflowException>(() => min.Subtract(one));
        Assert.Throws<OverflowException>(() => min.Negate());
    }

    // «`FromFrames` redondea hacia arriba»
    [Fact]
    public void FromFrames_RoundsUp()
    {
        // Con esta tasa el cuadro no dura un número entero de ticks.
        var rate = new Rational(705_600_001, 1_000_000);
        Int128 exact = (Int128)3 * Time.TicksPerSecond * rate.Den;
        long floor = (long)(exact / rate.Num);
        Assert.NotEqual(0, exact % rate.Num);
        Assert.Equal(floor + 1, Time.FromFrames(3, rate).Ticks);

        // También con cuadros negativos el redondeo va hacia +∞.
        Assert.Equal(-floor, Time.FromFrames(-3, rate).Ticks);
    }

    // «y `ToFrames` hacia abajo»
    [Fact]
    public void ToFrames_RoundsDown()
    {
        Time frame = Time.FromFrames(1, Rational.Fps25);
        Assert.Equal(0, Time.FromTicks(frame.Ticks - 1).ToFrames(Rational.Fps25));
        Assert.Equal(1, frame.ToFrames(Rational.Fps25));
        Assert.Equal(1, Time.FromTicks(frame.Ticks * 2 - 1).ToFrames(Rational.Fps25));
        Assert.Equal(-1, Time.FromTicks(-1).ToFrames(Rational.Fps25));
    }

    // «con cuentas intermedias en `Int128`»
    [Fact]
    public void FrameConversions_DoNotOverflowInIntermediateProducts()
    {
        // frame × 705 600 000 × 1001 no cabe en un long, pero el resultado sí.
        long frame = long.MaxValue / Time.TicksPerSecond * 30000 / 1001 / 2;
        Time t = Time.FromFrames(frame, Rational.Ntsc30);
        Assert.Equal(frame, t.ToFrames(Rational.Ntsc30));

        // Ticks × Num tampoco cabe en un long al final del rango.
        Assert.Equal(long.MaxValue / 23_543_520, Time.FromTicks(long.MaxValue).ToFrames(Rational.Ntsc30));
    }

    // «de modo que `FromFrames(n).ToFrames() == n` también con cadencias exóticas»
    [Fact]
    public void FromFramesThenToFrames_RoundTrips_WithExoticRates()
    {
        Rational[] rates =
        [
            Rational.Ntsc24, Rational.Ntsc30, Rational.Ntsc60,
            new(7, 3), new(1, 7), new(120_000, 1001), new(1_000_003, 999_983),
            new(705_600_001, 1_000_000), new(99_999_989, 1_234_567), new(1, 1_000_003),
        ];
        long[] frames = [-1_000_001, -2, -1, 0, 1, 2, 3, 59, 1001, 1_000_003, 123_456_789];
        var random = new Random(365);

        foreach (Rational rate in rates)
        {
            // Solo los cuadros cuyo inicio cabe en un long.
            long limit = (long)Int128.Min(50_000_000, (Int128)long.MaxValue * rate.Num / ((Int128)Time.TicksPerSecond * rate.Den) - 1);
            foreach (long n in frames)
            {
                if (Math.Abs(n) <= limit)
                {
                    Assert.Equal(n, Time.FromFrames(n, rate).ToFrames(rate));
                }
            }

            for (int i = 0; i < 2_000; i++)
            {
                long n = random.NextInt64(-limit, limit + 1);
                Assert.Equal(n, Time.FromFrames(n, rate).ToFrames(rate));
            }
        }
    }

    // «Además hay `SnapToFrame`»
    [Fact]
    public void SnapToFrame_GoesToNearestFrameStart()
    {
        Time frame = Time.FromFrames(1, Rational.Fps25);
        Assert.Equal(Time.Zero, Time.FromTicks(frame.Ticks / 2 - 1).SnapToFrame(Rational.Fps25));
        Assert.Equal(frame, Time.FromTicks(frame.Ticks / 2 + 1).SnapToFrame(Rational.Fps25));
        Assert.Equal(frame, Time.FromTicks(frame.Ticks + 1).SnapToFrame(Rational.Fps25));

        for (long n = -5; n <= 5; n++)
        {
            Time start = Time.FromFrames(n, Rational.Ntsc30);
            Assert.Equal(start, start.SnapToFrame(Rational.Ntsc30));
            Assert.Equal(start, Time.FromTicks(start.Ticks + 1).SnapToFrame(Rational.Ntsc30));
            Assert.Equal(start, Time.FromTicks(start.Ticks - 1).SnapToFrame(Rational.Ntsc30));
        }
    }

    // «`FromSamples`/`ToSamples`»
    [Fact]
    public void FromSamplesThenToSamples_RoundTrips()
    {
        int[] sampleRates = [8_000, 11_025, 22_050, 44_100, 48_000, 96_000, 192_000, 7, 44_101];
        long[] samples = [-48_001, -1, 0, 1, 2, 47_999, 48_000, 1_000_000_007];
        foreach (int rate in sampleRates)
        {
            foreach (long s in samples)
            {
                Assert.Equal(s, Time.FromSamples(s, rate).ToSamples(rate));
            }
        }

        Assert.Equal(44_099, Time.FromTicks(Time.TicksPerSecond - 1).ToSamples(44_100));
    }

    // «y `FromSeconds`, este solo para entradas humanas»
    [Fact]
    public void FromSeconds_RoundsToNearestTick()
    {
        Assert.Equal(Time.TicksPerSecond / 2, Time.FromSeconds(0.5).Ticks);
        Assert.Equal(-Time.TicksPerSecond * 3 / 2, Time.FromSeconds(-1.5).Ticks);
        Assert.Equal(1, Time.FromSeconds(0.9 / Time.TicksPerSecond).Ticks);
        Assert.Equal(0, Time.FromSeconds(0.4 / Time.TicksPerSecond).Ticks);
    }

    // «[`Rational`] es una fracción positiva»
    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(-1, 1)]
    [InlineData(1, -1)]
    [InlineData(-1, -1)]
    [InlineData(long.MinValue, 1)]
    [InlineData(1, long.MinValue)]
    public void Rational_RejectsNonPositive(long num, long den)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Rational(num, den));
    }

    // «y reducida para las tasas»
    [Theory]
    [InlineData(60000, 2002, 30000, 1001)]
    [InlineData(50, 2, 25, 1)]
    [InlineData(48000, 1000, 48, 1)]
    [InlineData(7, 7, 1, 1)]
    [InlineData(long.MaxValue, long.MaxValue, 1, 1)]
    public void Rational_IsReduced(long num, long den, long expectedNum, long expectedDen)
    {
        var r = new Rational(num, den);
        Assert.Equal(expectedNum, r.Num);
        Assert.Equal(expectedDen, r.Den);
        Assert.Equal(new Rational(expectedNum, expectedDen), r);
    }

    // «(`Ntsc30` = 30000/1001, `Fps25`...)»
    [Fact]
    public void Rational_NamedRates()
    {
        Assert.Equal((30000L, 1001L), (Rational.Ntsc30.Num, Rational.Ntsc30.Den));
        Assert.Equal((25L, 1L), (Rational.Fps25.Num, Rational.Fps25.Den));
        Assert.Equal((24000L, 1001L), (Rational.Ntsc24.Num, Rational.Ntsc24.Den));
        Assert.Equal((60000L, 1001L), (Rational.Ntsc60.Num, Rational.Ntsc60.Den));
        Assert.Equal((24L, 1L), (Rational.Fps24.Num, Rational.Fps24.Den));
        Assert.Equal((30L, 1L), (Rational.Fps30.Num, Rational.Fps30.Den));
        Assert.Equal((50L, 1L), (Rational.Fps50.Num, Rational.Fps50.Den));
        Assert.Equal((60L, 1L), (Rational.Fps60.Num, Rational.Fps60.Den));
    }

    // «nunca un decimal»
    [Fact]
    public void Rational_KeepsExactIntegersNotADecimal()
    {
        Assert.IsType<long>(Rational.Ntsc30.Num);
        Assert.IsType<long>(Rational.Ntsc30.Den);
        Assert.Equal("30000/1001", Rational.Ntsc30.ToString());
        Assert.NotEqual(new Rational(2997, 100), Rational.Ntsc30);
    }
}
