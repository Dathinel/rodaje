using CsCheck;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Tests.Timing;

public class TimePropertyTests
{
    // Tasas arbitrarias acotadas para que FromFrames quepa en un long:
    // |n|·den/num·705 600 000 ≤ 10⁶·10⁴·7,056·10⁸ ≈ 7·10¹⁸ < 9,2·10¹⁸.
    private static readonly Gen<Rational> GenRate =
        Gen.Select(Gen.Long[1, 1_000_000], Gen.Long[1, 10_000], (n, d) => new Rational(n, d));

    private static readonly Gen<long> GenFrame = Gen.Long[-1_000_000, 1_000_000];

    private static readonly Gen<int> GenSampleRate = Gen.Int[1, 1_000_000];

    private static readonly Gen<long> GenSample = Gen.Long[-1_000_000_000, 1_000_000_000];

    [Fact]
    public void FromFrames_ToFrames_RoundTrips()
    {
        Gen.Select(GenFrame, GenRate).Sample((n, rate) =>
            Assert.Equal(n, Time.FromFrames(n, rate).ToFrames(rate)));
    }

    [Fact]
    public void FromFrames_ToFrames_RoundTripsForNonNegativeFrames()
    {
        Gen.Select(Gen.Long[0, 1_000_000], GenRate).Sample((n, rate) =>
        {
            Time t = Time.FromFrames(n, rate);
            Assert.True(t >= Time.Zero);
            Assert.Equal(n, t.ToFrames(rate));
        });
    }

    [Fact]
    public void FromFrames_IsStrictlyMonotonic()
    {
        Gen.Select(GenFrame, Gen.Long[1, 1000], GenRate).Sample((n, delta, rate) =>
            Assert.True(Time.FromFrames(n, rate) < Time.FromFrames(n + delta, rate)));
    }

    [Fact]
    public void ToFrames_IsMonotonic()
    {
        Gen.Select(Gen.Long[-1_000_000_000_000, 1_000_000_000_000], Gen.Long[0, 1_000_000_000], GenRate)
            .Sample((ticks, delta, rate) =>
                Assert.True(Time.FromTicks(ticks).ToFrames(rate) <= Time.FromTicks(ticks + delta).ToFrames(rate)));
    }

    [Fact]
    public void ToFrames_FloorsEveryInstantInsideTheFrame()
    {
        Gen.Select(GenFrame, GenRate, Gen.Double[0, 1]).Sample((n, rate, fraction) =>
        {
            Time start = Time.FromFrames(n, rate);
            Time next = Time.FromFrames(n + 1, rate);
            long length = next.Ticks - start.Ticks;
            long offset = Math.Min(length - 1, (long)(fraction * length));
            Time inside = Time.FromTicks(start.Ticks + offset);

            Assert.Equal(n, inside.ToFrames(rate));
            Assert.Equal(n + 1, next.ToFrames(rate));
            Assert.Equal(n - 1, Time.FromTicks(start.Ticks - 1).ToFrames(rate));
        });
    }

    [Fact]
    public void SnapToFrame_ReturnsNearestFrameStart()
    {
        Gen.Select(Gen.Long[-1_000_000_000_000, 1_000_000_000_000], GenRate).Sample((ticks, rate) =>
        {
            Time t = Time.FromTicks(ticks);
            Time snapped = t.SnapToFrame(rate);
            long frame = t.ToFrames(rate);
            Time floor = Time.FromFrames(frame, rate);
            Time next = Time.FromFrames(frame + 1, rate);

            Assert.True(snapped == floor || snapped == next);
            Assert.Equal(snapped, Time.FromFrames(snapped.ToFrames(rate), rate));

            // En empate gana el cuadro posterior.
            long toFloor = ticks - floor.Ticks;
            long toNext = next.Ticks - ticks;
            Assert.Equal(toFloor < toNext ? floor : next, snapped);

            Assert.Equal(snapped, snapped.SnapToFrame(rate));
        });
    }

    [Fact]
    public void FromSamples_ToSamples_RoundTrips()
    {
        Gen.Select(GenSample, GenSampleRate).Sample((s, sampleRate) =>
        {
            Time t = Time.FromSamples(s, sampleRate);
            Assert.Equal(s, t.ToSamples(sampleRate));
            Assert.Equal(s - 1, Time.FromTicks(t.Ticks - 1).ToSamples(sampleRate));
        });
    }

    [Fact]
    public void FromSamples_IsStrictlyMonotonic()
    {
        Gen.Select(GenSample, Gen.Long[1, 1000], GenSampleRate).Sample((s, delta, sampleRate) =>
            Assert.True(Time.FromSamples(s, sampleRate) < Time.FromSamples(s + delta, sampleRate)));
    }

    [Fact]
    public void Addition_IsExactAndInvertible()
    {
        Gen.Select(Gen.Long[-1_000_000_000_000_000, 1_000_000_000_000_000], Gen.Long[-1_000_000_000_000_000, 1_000_000_000_000_000])
            .Sample((x, y) =>
            {
                var a = Time.FromTicks(x);
                var b = Time.FromTicks(y);
                Assert.Equal(a, a + b - b);
                Assert.Equal(a + b, b + a);
                Assert.Equal(a - b, -(b - a));
                Assert.Equal(Time.Min(a, b) + Time.Max(a, b), a + b);
                Assert.Equal(Math.Sign(x.CompareTo(y)), Math.Sign(a.CompareTo(b)));
                Assert.Equal(x < y, a < b);
            });
    }

    [Fact]
    public void FromSeconds_ToSeconds_RoundTripsWithinHalfTick()
    {
        Gen.Double[-100_000, 100_000].Sample(seconds =>
        {
            double back = Time.FromSeconds(seconds).ToSeconds();
            // Medio tick por el redondeo, más unos ulp del double (crecen con la magnitud).
            double tolerance = (0.5 / Time.TicksPerSecond) + (8 * double.Epsilon) + (8e-16 * Math.Abs(seconds));
            Assert.True(Math.Abs(back - seconds) <= tolerance);
        });
    }
}
