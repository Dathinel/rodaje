using CsCheck;
using FotoToma.Core.Timing;

namespace FotoToma.Core.Tests.Timing;

public class RationalTests
{
    [Theory]
    [InlineData(60000, 2002, 30000, 1001)]
    [InlineData(30, 1, 30, 1)]
    [InlineData(50, 2, 25, 1)]
    [InlineData(48000, 2002, 24000, 1001)]
    [InlineData(7, 14, 1, 2)]
    [InlineData(1, 1, 1, 1)]
    public void Constructor_NormalizesByGcd(long num, long den, long expectedNum, long expectedDen)
    {
        var r = new Rational(num, den);

        Assert.Equal(expectedNum, r.Num);
        Assert.Equal(expectedDen, r.Den);
    }

    [Fact]
    public void EquivalentFractions_AreEqual()
    {
        var a = new Rational(60000, 2002);
        var b = new Rational(30000, 1001);

        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.Equal(Rational.Ntsc30, a);
        Assert.NotEqual(Rational.Fps30, a);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(0, 0)]
    [InlineData(-30, 1)]
    [InlineData(30, -1)]
    [InlineData(-30, -1)]
    [InlineData(long.MinValue, 1)]
    public void Constructor_RejectsNonPositive(long num, long den)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new Rational(num, den));
        Assert.Contains($"{num}/{den}", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_AcceptsLargeValues()
    {
        var r = new Rational(long.MaxValue, long.MaxValue);
        Assert.Equal(new Rational(1, 1), r);

        var big = new Rational(long.MaxValue, 1);
        Assert.Equal(long.MaxValue, big.Num);
        Assert.Equal(1, big.Den);
    }

    [Theory]
    [InlineData(30, 1, "30")]
    [InlineData(30000, 1001, "30000/1001")]
    [InlineData(60000, 2002, "30000/1001")]
    [InlineData(50, 2, "25")]
    [InlineData(1, 2, "1/2")]
    public void ToString_ShowsIntegerOrFraction(long num, long den, string expected)
    {
        Assert.Equal(expected, new Rational(num, den).ToString());
    }

    [Fact]
    public void ToDouble_DividesNumByDen()
    {
        Assert.Equal(30.0, Rational.Fps30.ToDouble());
        Assert.Equal(30000.0 / 1001.0, Rational.Ntsc30.ToDouble());
        Assert.Equal(29.97, Rational.Ntsc30.ToDouble(), 2);
        Assert.Equal(0.5, new Rational(1, 2).ToDouble());
    }

    [Fact]
    public void PredefinedRates_HaveExpectedValues()
    {
        Assert.Equal((24000L, 1001L), (Rational.Ntsc24.Num, Rational.Ntsc24.Den));
        Assert.Equal((24L, 1L), (Rational.Fps24.Num, Rational.Fps24.Den));
        Assert.Equal((25L, 1L), (Rational.Fps25.Num, Rational.Fps25.Den));
        Assert.Equal((30000L, 1001L), (Rational.Ntsc30.Num, Rational.Ntsc30.Den));
        Assert.Equal((30L, 1L), (Rational.Fps30.Num, Rational.Fps30.Den));
        Assert.Equal((50L, 1L), (Rational.Fps50.Num, Rational.Fps50.Den));
        Assert.Equal((60000L, 1001L), (Rational.Ntsc60.Num, Rational.Ntsc60.Den));
        Assert.Equal((60L, 1L), (Rational.Fps60.Num, Rational.Fps60.Den));
    }

    [Fact]
    public void Property_NormalizedFormIsCoprimeAndPreservesValue()
    {
        Gen.Select(Gen.Long[1, 1_000_000], Gen.Long[1, 1_000_000], Gen.Long[1, 1000]).Sample((n, d, k) =>
        {
            var r = new Rational(n, d);
            Assert.Equal(1, System.Numerics.BigInteger.GreatestCommonDivisor(r.Num, r.Den));
            // n/d == r.Num/r.Den  <=>  n * r.Den == d * r.Num
            Assert.Equal((Int128)n * r.Den, (Int128)d * r.Num);
            Assert.Equal(r, new Rational(n * k, d * k));
        });
    }
}
