using System.Numerics;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Tests.Timing;

/// <summary><see cref="RetimeMath"/> frente a una referencia con <see cref="BigInteger"/> que integra la velocidad tramo a tramo.</summary>
public class RetimeMathTests
{
    private const long Max = RetimeMath.MaxExtent;

    [Fact]
    public void MillionEvaluations_MatchTheBigIntegerReference()
    {
        var random = new Random(5_0003);
        int evaluations = 0;
        while (evaluations < 1_000_000)
        {
            (RetimeMath math, Reference reference) = RandomSpeed(random);
            for (int i = 0; i < 250; i++)
            {
                long u = RandomArgument(random, reference.Span);
                Fraction exact = reference.Integral(u);
                Assert.Equal(exact.Floor(), math.Floor(u));
                Assert.Equal(exact.Ceiling(), math.Ceiling(u));

                long d = RandomArgument(random, reference.Span) / 4;
                Assert.Equal((exact + (reference.Rate(u) * d)).Floor(), math.FloorExtended(u, d));
                Fraction rate = reference.Rate(u);
                (Int128 num, Int128 den) = math.Rate(u);
                Assert.Equal(rate.Num * (BigInteger)den, (BigInteger)num * rate.Den);

                long target = (long)BigInteger.Clamp(exact.Floor() + random.NextInt64(-3, 4), -RetimeMath.MaxTarget, RetimeMath.MaxTarget);
                long inverse = math.Inverse(target);
                Assert.True(reference.Integral(inverse) >= target, $"I({inverse}) < {target}");
                Assert.True(reference.Integral(inverse - 1) < target, $"I({inverse - 1}) ≥ {target}");
                evaluations += 4;
            }
        }
    }

    [Theory]
    [InlineData(1, 10)]
    [InlineData(100, 1)]
    [InlineData(RetimeMath.MaxRateTerm, RetimeMath.MaxRateTerm - 1)]
    [InlineData(RetimeMath.MaxRateTerm / 10, RetimeMath.MaxRateTerm)]
    [InlineData(1001, 1000)]
    public void ConstantExtremes_AreExact(long num, long den)
    {
        RetimeMath math = RetimeMath.Constant(new Rational(num, den));
        var rate = new Rational(num, den);
        foreach (long u in new[] { -RetimeMath.MaxArgument, -Max, -1, 0, 1, Max - 1, Max, RetimeMath.MaxArgument })
        {
            Assert.Equal(new Fraction((BigInteger)u * rate.Num, rate.Den).Floor(), math.Floor(u));
            Assert.Equal(new Fraction((BigInteger)u * rate.Num, rate.Den).Ceiling(), math.Ceiling(u));
        }

        Assert.Equal(0, math.Inverse(0));
        Assert.Equal(rate.ToDouble(), math.RateAt(123), 12);
    }

    [Fact]
    public void CurveExtremes_AreExact()
    {
        // La curva más larga, de 0,1× a 10× y vuelta, evaluada en los bordes del rango.
        var curve = new SpeedCurve
        {
            Span = Time.FromTicks(Max),
            Points = [new(0, RetimeMath.MinHundredths), new(1, RetimeMath.MaxHundredths), new(9_999, RetimeMath.MaxHundredths), new(SpeedCurve.AtScale, RetimeMath.MinHundredths)],
        };
        RetimeMath math = RetimeMath.FromCurve(curve);
        Reference reference = Reference.Of(curve);
        foreach (long u in new[] { -RetimeMath.MaxArgument, -1, 0, 1, Max / 10_000, Max / 2, Max - 1, Max, Max + 1, RetimeMath.MaxArgument })
        {
            Assert.Equal(reference.Integral(u).Floor(), math.Floor(u));
            Assert.Equal(reference.Integral(u).Ceiling(), math.Ceiling(u));
            long target = (long)BigInteger.Clamp(reference.Integral(u).Floor(), -RetimeMath.MaxTarget, RetimeMath.MaxTarget);
            Assert.True(math.Inverse(target) <= u);
        }

        Assert.Equal(0.1, math.RateAt(0), 12);
        Assert.Equal(10, math.RateAt(Max / 2), 12);
        Assert.Equal(0.1, math.RateAt(Max + 5), 12);
    }

    [Fact]
    public void Identity_IsOneToOne()
    {
        Assert.True(RetimeMath.Identity.IsIdentity);
        Assert.Same(RetimeMath.Identity, RetimeMath.Constant(new Rational(3, 3)));
        Assert.Same(RetimeMath.Identity, RetimeMath.Of(null));
        Assert.Same(RetimeMath.Identity, RetimeMath.Of(new ClipSpeed { Freeze = true }));
        Assert.Equal(-7, RetimeMath.Identity.Floor(-7));
        Assert.Equal(12, RetimeMath.Identity.Inverse(12));
    }

    [Fact]
    public void InvalidInputs_Throw()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RetimeMath.Constant(new Rational(1, 11)));
        Assert.Throws<ArgumentOutOfRangeException>(() => RetimeMath.Constant(new Rational(101, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => RetimeMath.Constant(new Rational(RetimeMath.MaxRateTerm + 1, RetimeMath.MaxRateTerm)));
        Assert.Throws<ArgumentOutOfRangeException>(() => RetimeMath.Constant(default));
        Assert.Throws<ArgumentException>(() => RetimeMath.FromCurve(new SpeedCurve { Span = Time.FromTicks(10), Points = [new(0, 100)] }));
        Assert.Throws<ArgumentOutOfRangeException>(() => RetimeMath.Identity.Floor(RetimeMath.MaxArgument + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => RetimeMath.Identity.FloorExtended(0, -RetimeMath.MaxArgument - 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => RetimeMath.Identity.Inverse(RetimeMath.MaxTarget + 1));
    }

    public static TheoryData<string, SpeedCurve> BadCurves => new()
    {
        { "duración", new SpeedCurve { Span = Time.Zero, Points = [new(0, 100), new(10_000, 100)] } },
        { "duración", new SpeedCurve { Span = Time.FromTicks(Max + 1), Points = [new(0, 100), new(10_000, 100)] } },
        { "puntos", new SpeedCurve { Span = Time.FromTicks(10), Points = [new(0, 100)] } },
        { "puntos", new SpeedCurve { Span = Time.FromTicks(10), Points = [.. Enumerable.Range(0, 65).Select(i => new SpeedPoint(i, 100))] } },
        { "vacío", new SpeedCurve { Span = Time.FromTicks(10), Points = [new(0, 100), null!, new(10_000, 100)] } },
        { "empezar", new SpeedCurve { Span = Time.FromTicks(10), Points = [new(1, 100), new(10_000, 100)] } },
        { "empezar", new SpeedCurve { Span = Time.FromTicks(10), Points = [new(0, 100), new(9_999, 100)] } },
        { "0,1×", new SpeedCurve { Span = Time.FromTicks(10), Points = [new(0, 9), new(10_000, 100)] } },
        { "0,1×", new SpeedCurve { Span = Time.FromTicks(10), Points = [new(0, 100), new(10_000, 1001)] } },
        { "orden", new SpeedCurve { Span = Time.FromTicks(10), Points = [new(0, 100), new(5_000, 100), new(5_000, 100), new(10_000, 100)] } },
    };

    [Theory]
    [MemberData(nameof(BadCurves))]
    public void CurveProblem_NamesTheRule(string expected, SpeedCurve curve) =>
        Assert.Contains(expected, RetimeMath.CurveProblem(curve), StringComparison.Ordinal);

    [Fact]
    public void Equality_ComparesTheShape()
    {
        SpeedCurve curve = Ramp(1000);
        Assert.Equal(RetimeMath.FromCurve(curve), RetimeMath.FromCurve(curve.Clone()));
        Assert.Equal(RetimeMath.FromCurve(curve).GetHashCode(), RetimeMath.FromCurve(curve.Clone()).GetHashCode());
        Assert.NotEqual(RetimeMath.FromCurve(curve), RetimeMath.FromCurve(Ramp(1001)));
        Assert.NotEqual(RetimeMath.FromCurve(curve), RetimeMath.Identity);
        Assert.NotEqual(RetimeMath.Identity, RetimeMath.FromCurve(curve));
        Assert.Equal(RetimeMath.Constant(new Rational(2, 1)), RetimeMath.Constant(new Rational(4, 2)));
        Assert.False(RetimeMath.Identity.Equals((object?)null));
        Assert.True(RetimeMath.FromCurve(curve).IsCurve);
    }

    [Fact]
    public void Sqrt_IsTheFloorRoot()
    {
        var random = new Random(7);
        foreach (Int128 n in new Int128[] { -5, 0, 1, 2, 3, 4, 15, 16, 17, Int128.MaxValue, (Int128)long.MaxValue * long.MaxValue })
        {
            Check(n);
        }

        for (int i = 0; i < 10_000; i++)
        {
            Check(((Int128)random.NextInt64() << random.Next(0, 63)) + random.NextInt64());
        }

        static void Check(Int128 n)
        {
            Int128 r = RetimeMath.Sqrt(n);
            if (n < 0)
            {
                Assert.Equal(0, r);
                return;
            }

            Assert.True((BigInteger)r * r <= n && ((BigInteger)r + 1) * ((BigInteger)r + 1) > n);
        }
    }

    internal static SpeedCurve Ramp(long span) => new()
    {
        Span = Time.FromTicks(span),
        Points = [new(0, 50), new(5_000, 400), new(SpeedCurve.AtScale, 100)],
    };

    private static (RetimeMath, Reference) RandomSpeed(Random random)
    {
        if (random.Next(4) == 0)
        {
            long den = random.Next(3) == 0 ? random.NextInt64(1, RetimeMath.MaxRateTerm + 1) : random.NextInt64(1, 1001);
            long num = random.NextInt64(Math.Max(1, (den + 9) / 10), Math.Min(RetimeMath.MaxRateTerm, den * 100) + 1);
            var rate = new Rational(num, den);
            return (RetimeMath.Constant(rate), Reference.Of(rate));
        }

        SpeedCurve curve = RandomCurve(random);
        return (RetimeMath.FromCurve(curve), Reference.Of(curve));
    }

    internal static SpeedCurve RandomCurve(Random random)
    {
        long span = random.Next(3) switch
        {
            0 => random.NextInt64(1, 20_000),
            1 => Max - random.NextInt64(0, 1000),
            _ => (long)Math.Pow(2, random.NextDouble() * 44),
        };
        int count = random.Next(2, 9);
        var ats = new SortedSet<int> { 0, SpeedCurve.AtScale };
        while (ats.Count < count)
        {
            ats.Add(random.Next(1, SpeedCurve.AtScale));
        }

        return new SpeedCurve
        {
            Span = Time.FromTicks(Math.Max(1, span)),
            Points = [.. ats.Select(at => new SpeedPoint(at, random.Next(5) switch
            {
                0 => RetimeMath.MinHundredths,
                1 => RetimeMath.MaxHundredths,
                _ => random.Next(RetimeMath.MinHundredths, RetimeMath.MaxHundredths + 1),
            }))],
        };
    }

    private static long RandomArgument(Random random, long span) => random.Next(6) switch
    {
        0 => random.NextInt64(-RetimeMath.MaxArgument, RetimeMath.MaxArgument + 1),
        1 => random.NextInt64(-1000, 1000),
        2 => span + random.NextInt64(-1000, 1000),
        3 => random.NextInt64(0, Math.Max(1, span)),
        4 => (long)(Math.Pow(2, random.NextDouble() * 46) * (random.Next(2) * 2 - 1)),
        _ => random.NextInt64(-Max, Max + 1),
    };

    /// <summary>Fracción exacta con denominador positivo.</summary>
    internal readonly record struct Fraction(BigInteger Num, BigInteger Den)
    {
        public static Fraction operator +(Fraction a, Fraction b) => new((a.Num * b.Den) + (b.Num * a.Den), a.Den * b.Den);

        public static Fraction operator *(Fraction a, long b) => new(a.Num * b, a.Den);

        public static bool operator >=(Fraction a, long b) => a.Num >= b * a.Den;

        public static bool operator <=(Fraction a, long b) => a.Num <= b * a.Den;

        public static bool operator <(Fraction a, long b) => a.Num < b * a.Den;

        public static bool operator >(Fraction a, long b) => a.Num > b * a.Den;

        public BigInteger Floor() => BigInteger.DivRem(Num, Den) is var (q, r) && r.Sign < 0 ? q - 1 : q;

        public BigInteger Ceiling() => -new Fraction(-Num, Den).Floor();
    }

    /// <summary>La velocidad como función: constante, o interpolada entre nudos y constante fuera de ellos.</summary>
    internal sealed class Reference
    {
        private readonly BigInteger[] _at = [];
        private readonly int[] _h = [];
        private readonly Rational? _rate;

        private Reference(Rational rate) => _rate = rate;

        private Reference(BigInteger[] at, int[] h)
        {
            _at = at;
            _h = h;
        }

        public long Span => _rate is null ? (long)_at[^1] : 0;

        public static Reference Of(Rational rate) => new(rate);

        public static Reference Of(SpeedCurve curve) => new(
            [.. curve.Points.Select(p => (BigInteger)p.At * curve.Span.Ticks / SpeedCurve.AtScale)],
            [.. curve.Points.Select(p => p.Hundredths)]);

        public Fraction Integral(long u)
        {
            if (_rate is { } rate)
            {
                return new Fraction((BigInteger)u * rate.Num, rate.Den);
            }

            if (u <= 0)
            {
                return new Fraction((BigInteger)u * _h[0], 100);
            }

            var sum = new Fraction(0, 1);
            for (int k = 0; k + 1 < _at.Length && _at[k] < u; k++)
            {
                BigInteger length = _at[k + 1] - _at[k];
                if (length.IsZero)
                {
                    continue;
                }

                BigInteger x = BigInteger.Min(u, _at[k + 1]) - _at[k];
                // ∫₀ˣ (h_k + Δh·y/L) dy / 100 = (2·L·h_k·x + Δh·x²) / (200·L)
                sum += new Fraction((2 * length * _h[k] * x) + ((_h[k + 1] - _h[k]) * x * x), 200 * length);
            }

            if (u > _at[^1])
            {
                sum += new Fraction((u - _at[^1]) * _h[^1], 100);
            }

            return sum;
        }

        public Fraction Rate(long u)
        {
            if (_rate is { } rate)
            {
                return new Fraction(rate.Num, rate.Den);
            }

            if (u <= 0)
            {
                return new Fraction(_h[0], 100);
            }

            if (u >= _at[^1])
            {
                return new Fraction(_h[^1], 100);
            }

            int k = Array.FindLastIndex(_at, a => a <= u);
            BigInteger length = _at[k + 1] - _at[k];
            return new Fraction((_h[k] * length) + ((_h[k + 1] - _h[k]) * (u - _at[k])), 100 * length);
        }
    }
}
