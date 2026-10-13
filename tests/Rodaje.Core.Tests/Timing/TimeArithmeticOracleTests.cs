using System.Numerics;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Tests.Timing;

/// <summary>
/// Pruebas de propiedades con semilla fija contra un oráculo en <see cref="BigInteger"/>: cada conversión de
/// <see cref="Time"/> da el valor exacto (redondeado como dice su contrato) o lanza <see cref="OverflowException"/> si
/// no cabe en un <c>long</c>, sin desbordes silenciosos de los productos intermedios.
/// </summary>
public class TimeArithmeticOracleTests
{
    private const int Seed = 20261013;
    private const int Cases = 20_000;
    private static readonly BigInteger T = Time.TicksPerSecond;

    private static readonly Rational[] StandardRates =
    [
        Rational.Ntsc24, Rational.Fps24, Rational.Fps25, Rational.Ntsc30, Rational.Fps30, Rational.Fps50, Rational.Ntsc60,
        Rational.Fps60, new(48, 1), new(120, 1), new(120000, 1001), new(15, 1), new(12, 1), new(1, 1),
    ];

    private static readonly int[] StandardSampleRates = [8000, 11025, 16000, 22050, 32000, 44100, 48000, 88200, 96000, 176400, 192000];

    // ---- Oráculo ----

    private static BigInteger FloorDiv(BigInteger a, BigInteger b)
    {
        BigInteger q = BigInteger.DivRem(a, b, out BigInteger r);
        return r != 0 && (r.Sign < 0) != (b.Sign < 0) ? q - 1 : q;
    }

    private static BigInteger CeilDiv(BigInteger a, BigInteger b) => -FloorDiv(-a, b);

    private static bool Fits(BigInteger v) => v >= long.MinValue && v <= long.MaxValue;

    private static BigInteger FrameStart(BigInteger frame, Rational rate) => CeilDiv(frame * T * rate.Den, rate.Num);

    private static BigInteger FrameOf(BigInteger ticks, Rational rate) => FloorDiv(ticks * rate.Num, T * rate.Den);

    private static BigInteger SnapOracle(long ticks, Rational rate)
    {
        BigInteger frame = FrameOf(ticks, rate);
        BigInteger floor = FrameStart(frame, rate);
        BigInteger next = FrameStart(frame + 1, rate);
        return (ticks - floor) * 2 < next - floor ? floor : next;
    }

    /// <summary>Compara con el oráculo: el valor exacto si cabe, <see cref="OverflowException"/> si no.</summary>
    private static void AssertExactOrOverflow(BigInteger expected, Func<long> actual, string what)
    {
        if (Fits(expected))
        {
            long got;
            try
            {
                got = actual();
            }
            catch (OverflowException e)
            {
                Assert.Fail($"{what}: lanzó {e.GetType().Name} pero el resultado {expected} cabe en un long.");
                return;
            }

            Assert.True((BigInteger)got == expected, $"{what}: dio {got}, el exacto es {expected}.");
        }
        else
        {
            long got = 0;
            bool threw = false;
            try
            {
                got = actual();
            }
            catch (OverflowException)
            {
                threw = true;
            }

            Assert.True(threw, $"{what}: el exacto {expected} no cabe en un long y devolvió {got} en silencio.");
        }
    }

    // ---- Generadores ----

    /// <summary>Un entero de magnitud repartida en escala logarítmica, con signo, incluidos los extremos.</summary>
    private static long NextLong(Random random, int maxBits = 63)
    {
        switch (random.Next(20))
        {
            case 0:
                return long.MaxValue - random.Next(3);
            case 1:
                return long.MinValue + random.Next(3);
            case 2:
                return random.Next(-2, 3);
        }

        long magnitude = NextPositive(random, maxBits);
        return random.Next(2) == 0 ? magnitude : -magnitude;
    }

    private static long NextPositive(Random random, int maxBits)
    {
        int bits = random.Next(1, maxBits + 1);
        return bits >= 63 ? random.NextInt64(1, long.MaxValue) : random.NextInt64(1L << (bits - 1), 1L << bits);
    }

    /// <summary>Tasas estándar, casi estándar (×1000/1001) y arbitrarias de términos hasta 2^62, que un .rodaje admite.</summary>
    private static Rational NextRate(Random random) => random.Next(4) switch
    {
        0 => StandardRates[random.Next(StandardRates.Length)],
        1 => new Rational(NextPositive(random, 20), NextPositive(random, 12)),
        2 => new Rational(NextPositive(random, 62), NextPositive(random, 62)),
        _ => new Rational(NextPositive(random, 31), NextPositive(random, 31)),
    };

    // ---- Propiedades ----

    [Fact]
    public void FromFrames_MatchesOracleOrThrows()
    {
        var random = new Random(Seed);
        for (int i = 0; i < Cases; i++)
        {
            long frame = NextLong(random);
            Rational rate = NextRate(random);
            AssertExactOrOverflow(FrameStart(frame, rate), () => Time.FromFrames(frame, rate).Ticks, $"FromFrames({frame}, {rate})");
        }
    }

    [Fact]
    public void ToFrames_MatchesOracleOrThrows()
    {
        var random = new Random(Seed + 1);
        for (int i = 0; i < Cases; i++)
        {
            long ticks = NextLong(random);
            Rational rate = NextRate(random);
            AssertExactOrOverflow(FrameOf(ticks, rate), () => Time.FromTicks(ticks).ToFrames(rate), $"ToFrames({ticks}, {rate})");
        }
    }

    [Fact]
    public void SnapToFrame_MatchesOracleOrThrows()
    {
        var random = new Random(Seed + 2);
        for (int i = 0; i < Cases; i++)
        {
            long ticks = NextLong(random);
            Rational rate = NextRate(random);
            AssertExactOrOverflow(SnapOracle(ticks, rate), () => Time.FromTicks(ticks).SnapToFrame(rate).Ticks, $"SnapToFrame({ticks}, {rate})");
        }
    }

    [Fact]
    public void Samples_MatchOracleOrThrow()
    {
        var random = new Random(Seed + 3);
        for (int i = 0; i < Cases; i++)
        {
            long value = NextLong(random);
            int sampleRate = random.Next(2) == 0 ? StandardSampleRates[random.Next(StandardSampleRates.Length)] : (int)NextPositive(random, 31);
            AssertExactOrOverflow(CeilDiv(value * T, sampleRate), () => Time.FromSamples(value, sampleRate).Ticks, $"FromSamples({value}, {sampleRate})");
            AssertExactOrOverflow(FloorDiv(value * (BigInteger)sampleRate, T), () => Time.FromTicks(value).ToSamples(sampleRate), $"ToSamples({value}, {sampleRate})");
        }
    }

    [Fact]
    public void FrameRoundTrip_IsExactWhenAFrameLastsAtLeastATick()
    {
        var random = new Random(Seed + 4);
        for (int i = 0; i < Cases; i++)
        {
            Rational rate = NextRate(random);
            if ((BigInteger)rate.Num > T * rate.Den)
            {
                continue;
            }

            long frame = NextLong(random);
            BigInteger start = FrameStart(frame, rate);
            BigInteger next = FrameStart((BigInteger)frame + 1, rate);
            if (!Fits(start) || !Fits(next))
            {
                continue;
            }

            Time t = Time.FromFrames(frame, rate);
            Assert.Equal(frame, t.ToFrames(rate));
            Assert.Equal(frame, Time.FromTicks((long)next - 1).ToFrames(rate));
            if (start > long.MinValue)
            {
                Assert.Equal(frame - 1, Time.FromTicks(t.Ticks - 1).ToFrames(rate));
            }

            Assert.Equal(t, t.SnapToFrame(rate));
        }
    }

    [Fact]
    public void SampleRoundTrip_IsExactForEveryRateUpToTheTickRate()
    {
        var random = new Random(Seed + 5);
        for (int i = 0; i < Cases; i++)
        {
            int sampleRate = random.Next(2) == 0 ? StandardSampleRates[random.Next(StandardSampleRates.Length)] : random.Next(1, 705_600_001);
            // Con 1 Hz, 2^33 muestras ya son unos 6·10^18 ticks: el resto del rango no cabe y lo cubre Samples_MatchOracleOrThrow.
            long sample = random.NextInt64(-(1L << 33), 1L << 33);
            Time t = Time.FromSamples(sample, sampleRate);
            Assert.Equal(sample, t.ToSamples(sampleRate));
            Assert.Equal(sample - 1, Time.FromTicks(t.Ticks - 1).ToSamples(sampleRate));
        }
    }

    [Fact]
    public void ToFrames_And_ToSamples_AreMonotonic()
    {
        var random = new Random(Seed + 6);
        for (int i = 0; i < Cases; i++)
        {
            Rational rate = NextRate(random);
            int sampleRate = StandardSampleRates[random.Next(StandardSampleRates.Length)];
            long a = NextLong(random, 56);
            long b = NextLong(random, 56);
            (a, b) = a <= b ? (a, b) : (b, a);
            Assert.True(Time.FromTicks(a).ToSamples(sampleRate) <= Time.FromTicks(b).ToSamples(sampleRate));
            if (Fits(FrameOf(a, rate)) && Fits(FrameOf(b, rate)))
            {
                Assert.True(Time.FromTicks(a).ToFrames(rate) <= Time.FromTicks(b).ToFrames(rate));
            }
        }
    }

    [Theory]
    [InlineData(24000, 1001)]
    [InlineData(30000, 1001)]
    [InlineData(60000, 1001)]
    [InlineData(25, 1)]
    [InlineData(50, 1)]
    [InlineData(24, 1)]
    public void StandardRates_AreExactAtFrameEdgesUpToHundredsOfHours(long num, long den)
    {
        var rate = new Rational(num, den);
        // Las tasas NTSC dejan cuadros de 29 429 400 ticks (23,976), 23 543 520 (29,97) y 11 771 760 (59,94): exactos.
        Assert.Equal(0, T * den % num);
        long frameTicks = (long)(T * den / num);
        foreach (long hours in new long[] { 1, 10, 24, 100, 1000, 100_000 })
        {
            long frames = hours * 3600 * num / den;
            Time start = Time.FromFrames(frames, rate);
            Assert.Equal(frames * frameTicks, start.Ticks);
            Assert.Equal(frames, start.ToFrames(rate));
            Assert.Equal(frames - 1, Time.FromTicks(start.Ticks - 1).ToFrames(rate));
            Assert.Equal(-frames, (-start).ToFrames(rate));
            Assert.Equal(-frames - 1, Time.FromTicks(-start.Ticks - 1).ToFrames(rate));
            Assert.Equal(start, Time.FromTicks(start.Ticks + (frameTicks / 2) - 1).SnapToFrame(rate));
            Assert.Equal(start + Time.FromTicks(frameTicks), Time.FromTicks(start.Ticks + (frameTicks / 2)).SnapToFrame(rate));
            Assert.Equal(-start, Time.FromTicks(-start.Ticks - (frameTicks / 2) + 1).SnapToFrame(rate));
        }
    }

    [Theory]
    [InlineData(48000)]
    [InlineData(44100)]
    [InlineData(96000)]
    [InlineData(192000)]
    [InlineData(8000)]
    public void StandardSampleRates_AreExactUpToHundredsOfHours(int sampleRate)
    {
        long samplePeriod = Time.TicksPerSecond / sampleRate;
        Assert.Equal(0, Time.TicksPerSecond % sampleRate);
        foreach (long hours in new long[] { 24, 100, 10_000 })
        {
            long samples = hours * 3600 * sampleRate;
            Time t = Time.FromSamples(samples, sampleRate);
            Assert.Equal(samples * samplePeriod, t.Ticks);
            Assert.Equal(samples, t.ToSamples(sampleRate));
            Assert.Equal(-samples, (-t).ToSamples(sampleRate));
            Assert.Equal(-samples - 1, Time.FromTicks(-t.Ticks - 1).ToSamples(sampleRate));
        }
    }

    /// <summary>
    /// Regresión: con un denominador grande (un .rodaje admite cualquier tasa positiva de términos <c>long</c>) el
    /// numerador <c>frame·705 600 000·den</c> pasaba de 2^127 y se envolvía en silencio dentro de <see cref="Int128"/>:
    /// <see cref="Time.FromFrames"/> devolvía un instante inventado en vez de lanzar.
    /// </summary>
    [Fact]
    public void FromFrames_DoesNotWrapWhenTheIntermediateProductOverflowsInt128()
    {
        // Hallado con la semilla 20261013: devolvía 4 397 365 948 245 943 419 ticks (unos 197 años) en silencio.
        var rate = new Rational(4530321211787948537, 1155314566989633);
        Assert.Throws<OverflowException>(() => Time.FromFrames(-57997926778488053, rate));

        var nearOne = new Rational(long.MaxValue, long.MaxValue - 1);
        Assert.Throws<OverflowException>(() => Time.FromFrames(long.MaxValue, nearOne));
        Assert.Throws<OverflowException>(() => Time.FromFrames(long.MinValue, nearOne));
    }

    /// <summary>
    /// Regresión: <see cref="Time.SnapToFrame"/> calculaba siempre el inicio del cuadro siguiente, así que al final del
    /// rango lanzaba aunque el cuadro más cercano (el actual) sí cabía en un <c>long</c>.
    /// </summary>
    [Fact]
    public void SnapToFrame_AtTheEndsOfTheRange_ReturnsTheNearestFrameThatFits()
    {
        int fitting = 0;
        foreach (Rational rate in StandardRates)
        {
            foreach (long ticks in new[] { long.MaxValue, long.MaxValue - 1, long.MinValue, long.MinValue + 1 })
            {
                BigInteger expected = SnapOracle(ticks, rate);
                fitting += Fits(expected) ? 1 : 0;
                AssertExactOrOverflow(expected, () => Time.FromTicks(ticks).SnapToFrame(rate).Ticks, $"SnapToFrame({ticks}, {rate})");
            }
        }

        Assert.True(fitting > 0);

        // Hallado con la semilla 20261013 (un cuadro de menos de un tick): ToFrames no cabe, pero el inicio más cercano sí.
        var tiny = new Rational(17598638632448975, 67);
        Assert.Equal(5231032371856006845, Time.FromTicks(5231032371856006845).SnapToFrame(tiny).Ticks);
    }
}
