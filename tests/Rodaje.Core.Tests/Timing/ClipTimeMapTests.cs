using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Tests.Timing;

/// <summary>Propiedades de <see cref="ClipTimeMap"/> con velocidades constantes, curvas, reversa y congelado al azar.</summary>
public class ClipTimeMapTests
{
    private const int Cases = 3000;
    private const int Probes = 40;

    [Fact]
    public void RoundTrips_SourceOfTimelineOfIsAtLeastAndTimelineOfSourceIsAtMost()
    {
        var random = new Random(11);
        for (int c = 0; c < Cases; c++)
        {
            ClipTimeMap map = RandomMap(random);
            for (int i = 0; i < Probes; i++)
            {
                Time t = RandomInside(random, map);
                Time s = map.Source(t);
                InRange(s, map.SourceIn, map.SourceOut - Time.FromTicks(1));
                Assert.True(map.TimelineOf(s) <= t, $"{map}: T(source({t})) > t");

                Time wanted = map.SourceIn + Time.FromTicks(random.NextInt64(0, (map.SourceOut - map.SourceIn).Ticks));
                Time first = map.TimelineOf(wanted);
                InRange(first, map.Start, map.End);
                if (first < map.End)
                {
                    Assert.True(Reaches(map, map.Source(first), wanted), $"{map}: source(T({wanted})) no lo alcanza");
                }

                if (first > map.Start && !map.Freeze)
                {
                    Assert.False(Reaches(map, map.Source(first - Time.FromTicks(1)), wanted), $"{map}: T({wanted}) no es el primero");
                }
            }
        }
    }

    [Fact]
    public void Edges_AreExact()
    {
        var random = new Random(12);
        for (int c = 0; c < Cases; c++)
        {
            ClipTimeMap map = RandomMap(random);
            Time last = map.End - Time.FromTicks(1);
            if (map.Freeze)
            {
                Assert.Equal(map.SourceIn + Time.FromTicks(1), map.SourceOut);
                Assert.Equal(map.SourceIn, map.Source(map.Start));
                Assert.Equal(map.SourceIn, map.Source(last));
                Assert.Equal(0, map.RateAt(map.Start));
                continue;
            }

            Assert.Equal(map.Reverse ? map.SourceOut - Time.FromTicks(1) : map.SourceIn, map.Source(map.Start));
            InRange(map.Source(last), map.SourceIn, map.SourceOut - Time.FromTicks(1));
            Assert.Equal(map.Source(map.Start), map.Source(map.Start - Time.FromTicks(5)));
            Assert.Equal(map.Source(last), map.Source(map.End + Time.FromTicks(5)));
            Assert.Equal(map.Start, map.TimelineOf(map.Reverse ? map.SourceOut : map.SourceIn - Time.FromTicks(1)));
            Assert.Equal(map.End, map.TimelineOf(map.Reverse ? map.SourceIn - Time.FromTicks(1) : map.SourceOut));
            Assert.Equal(map.Reverse, map.RateAt(map.Start) < 0);
        }
    }

    [Fact]
    public void Source_IsMonotonic()
    {
        var random = new Random(13);
        for (int c = 0; c < Cases; c++)
        {
            ClipTimeMap map = RandomMap(random);
            Time a = RandomInside(random, map);
            Time b = RandomInside(random, map);
            (a, b) = a <= b ? (a, b) : (b, a);
            Assert.True(map.Reverse ? map.Source(a) >= map.Source(b) : map.Source(a) <= map.Source(b));
        }
    }

    [Fact]
    public void Split_KeepsEverySourceFrame()
    {
        var random = new Random(14);
        for (int c = 0; c < Cases; c++)
        {
            ClipTimeMap map = RandomMap(random);
            if (map.Duration.Ticks < 2)
            {
                continue;
            }

            Time at = map.Start + Time.FromTicks(random.NextInt64(1, map.Duration.Ticks));
            (ClipTimeMap left, ClipTimeMap right) = map.SplitAt(at);
            Assert.Equal((map.Start, at, at, map.End), (left.Start, left.End, right.Start, right.End));
            InRange(left.SourceIn, map.SourceIn, map.SourceOut);
            InRange(right.SourceOut, map.SourceIn, map.SourceOut + Time.FromTicks(1));
            for (int i = 0; i < Probes; i++)
            {
                Time t = RandomInside(random, map);
                ClipTimeMap half = t < at ? left : right;
                Assert.Equal(map.Source(t), half.Source(t));
                Assert.Equal(map.EffectTime(t), half.EffectTime(t));
            }

            Assert.Equal(map.Source(at - Time.FromTicks(1)), left.Source(at - Time.FromTicks(1)));
            Assert.Equal(map.Source(at), right.Source(at));
        }
    }

    [Fact]
    public void Join_UndoesASplitAndSameCourseHoldsForTheHalves()
    {
        var random = new Random(15);
        int joined = 0;
        for (int c = 0; c < Cases; c++)
        {
            ClipTimeMap map = RandomMap(random);
            if (map.Duration.Ticks < 2)
            {
                continue;
            }

            (ClipTimeMap left, ClipTimeMap right) = map.SplitAt(map.Start + Time.FromTicks(random.NextInt64(1, map.Duration.Ticks)));
            Assert.True(left.SameCourse(right) && right.SameCourse(left) && map.SameCourse(right), $"{map}");
            Assert.Equal(map, left.Join(right));
            Assert.Null(right.Join(left));
            Assert.Null(left.Join(right.MoveTo(right.Start + Time.FromTicks(1))));
            if (!map.Freeze)
            {
                Assert.False(left.SameCourse(right.Slip(Time.FromTicks(1))), $"{map}");
                Assert.Null(left.Join(right.Slip(Time.FromTicks(1))));
            }

            joined++;
        }

        Assert.True(joined > Cases / 2);
        ClipTimeMap plain = ClipTimeMap.Create(Time.Zero, Time.FromTicks(100), Time.FromTicks(50), null);
        ClipTimeMap slow = ClipTimeMap.Create(Time.FromTicks(100), Time.FromTicks(100), Time.FromTicks(150), new ClipSpeed { Rate = new Rational(1, 2) });
        Assert.False(plain.SameCourse(slow));
        Assert.Null(plain.Join(slow));
    }

    [Fact]
    public void RateRange_FindsThePeakInsideACurveAndTheEdgeRateOutside()
    {
        var peak = new SpeedCurve { Span = Time.FromTicks(10_000), Points = [new(0, 100), new(5000, 800), new(10_000, 50)] };
        ClipTimeMap map = ClipTimeMap.Create(Time.FromTicks(1000), Time.FromTicks(10_000), Time.Zero, new ClipSpeed { Curve = peak });

        // El último tick del clip va a 0,5015×: la curva baja hasta 0,5× en el final, que ya no es del clip.
        (double low, double high) = map.RateRange(map.Start, map.End);
        Assert.Equal(0.5015, low, 9);
        Assert.Equal(8.0, high);
        Assert.Equal((1.0, 1.0), map.RateRange(Time.Zero, map.Start));
        (double after, double afterMax) = map.RateRange(map.End, map.End + Time.FromTicks(500));
        Assert.Equal(low, after);
        Assert.Equal(low, afterMax);
        (double min, double max) = map.RateRange(map.Start, map.Start + Time.FromTicks(2500));
        Assert.Equal(1.0, min);
        Assert.Equal(4.4986, max, 4);
        Assert.Equal(map.Speed.RateRange(0, 10_000), map.Speed.RateRange(10_000, 0));

        var constant = ClipTimeMap.Create(Time.Zero, Time.FromTicks(10), Time.Zero, new ClipSpeed { Rate = new Rational(5, 2), Reverse = true });
        Assert.Equal((2.5, 2.5), constant.RateRange(Time.Zero, Time.FromTicks(10)));
        Assert.Equal((0.0, 0.0), ClipTimeMap.Create(Time.Zero, Time.FromTicks(10), Time.Zero, new ClipSpeed { Freeze = true }).RateRange(Time.Zero, Time.FromTicks(10)));
        Assert.Equal((1.0, 1.0), ClipTimeMap.Create(Time.Zero, Time.FromTicks(10), Time.Zero, null).RateRange(Time.Zero, Time.FromTicks(10)));
    }

    [Fact]
    public void TrimmingAndExtendingBack_RestoresTheSameMap()
    {
        var random = new Random(15);
        for (int c = 0; c < Cases; c++)
        {
            ClipTimeMap map = RandomMap(random);
            Time inside = map.Start + Time.FromTicks(random.NextInt64(0, map.Duration.Ticks));
            Assert.Equal(map, map.TrimIn(inside).TrimIn(map.Start));
            Assert.Equal(map, map.TrimOut(inside + Time.FromTicks(1)).TrimOut(map.End));
            Assert.Equal(map.Source(inside), map.TrimIn(inside).Source(inside));
            Assert.Equal(map, map.Slip(Time.FromTicks(1000)).Slip(Time.FromTicks(-1000)));
            Assert.Equal(map, map.MoveTo(map.Start + Time.FromTicks(7)).MoveTo(map.Start));
            Assert.Equal(map.Source(inside) + Time.FromTicks(3), map.Slip(Time.FromTicks(3)).Source(inside));
        }
    }

    [Fact]
    public void ExtendedSource_MatchesInsideAndFollowsTheEdgeOutside()
    {
        var random = new Random(16);
        for (int c = 0; c < Cases; c++)
        {
            ClipTimeMap map = RandomMap(random);
            Time t = RandomInside(random, map);
            Assert.Equal(map.Source(t), map.ExtendedSource(t));
            Time before = map.ExtendedSource(map.Start - Time.FromTicks(random.NextInt64(1, 1_000_000)));
            Time after = map.ExtendedSource(map.End + Time.FromTicks(random.NextInt64(0, 1_000_000)));
            if (map.Freeze)
            {
                Assert.Equal((map.SourceIn, map.SourceIn), (before, after));
            }
            else if (map.Reverse)
            {
                Assert.True(before >= map.SourceOut - Time.FromTicks(1) && after < map.SourceOut);
            }
            else
            {
                Assert.True(before <= map.SourceIn && after >= map.Source(map.End - Time.FromTicks(1)));
            }
        }
    }

    [Fact]
    public void ConstantRate_IsTheExpectedCount()
    {
        var clip = SpeedClip(new ClipSpeed { Rate = new Rational(2, 1) }, sourceIn: 1000, duration: 500);
        clip.SourceOut = Time.FromTicks(2000);
        ClipTimeMap map = clip.TimeMap;

        Assert.Equal(Time.FromTicks(2000), map.SourceOut);
        Assert.Equal(Time.FromTicks(1000 + (2 * 123)), map.Source(Time.FromTicks(10_123)));
        Assert.Equal(Time.FromTicks(10_123), map.TimelineOf(Time.FromTicks(1000 + (2 * 123) - 1)));
        Assert.Equal(2, map.RateAt(Time.FromTicks(10_100)));
        Assert.Equal(Time.FromTicks(1000 + (2 * 123)), clip.ToSourceTime(Time.FromTicks(10_123)));
        Assert.Equal(Time.FromTicks(1000 - 20), clip.ToSourceTime(Time.FromTicks(10_000 - 10)));
        Assert.Equal(Time.FromTicks(2000 + 20), clip.ToSourceTime(Time.FromTicks(10_500 + 10)));
        Assert.Equal(map.Source(Time.FromTicks(10_200)), clip.ToEffectTime(Time.FromTicks(10_200)));
        Assert.False(map.IsIdentity);
        Assert.Contains("→", map.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Reverse_RunsBackFromSourceOut()
    {
        var clip = SpeedClip(new ClipSpeed { Reverse = true }, sourceIn: 0, duration: 100);
        clip.SourceOut = Time.FromTicks(100);
        ClipTimeMap map = clip.TimeMap;

        Assert.Equal(Time.FromTicks(99), map.Source(Time.FromTicks(10_000)));
        Assert.Equal(Time.FromTicks(0), map.Source(Time.FromTicks(10_099)));
        Assert.Equal(Time.FromTicks(10_009), map.TimelineOf(Time.FromTicks(90)));
        Assert.Equal(-1, map.RateAt(Time.FromTicks(10_050)));
        Assert.Contains("◀", map.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Freeze_ShowsOneInstantAndAdvancesEffectTime()
    {
        var clip = SpeedClip(new ClipSpeed { Freeze = true, Offset = Time.FromTicks(40) }, sourceIn: 500, duration: 300);
        clip.SourceOut = Time.FromTicks(501);
        ClipTimeMap map = clip.TimeMap;

        Assert.Equal(Time.FromTicks(500), map.Source(Time.FromTicks(10_250)));
        Assert.Equal(Time.FromTicks(500 + 40 + 250), map.EffectTime(Time.FromTicks(10_250)));
        Assert.Equal(map.Start, map.TimelineOf(Time.FromTicks(500)));
        Assert.Equal(map.End, map.TimelineOf(Time.FromTicks(501)));
        Assert.Equal(Time.FromTicks(500), clip.ToSourceTime(Time.FromTicks(50)));
        Assert.Contains("congelado", map.ToString(), StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => ClipTimeMap.Fit(Time.Zero, Time.Zero, Time.FromTicks(10), clip.Speed));
    }

    [Fact]
    public void WithoutSpeed_IsTheFormat3Arithmetic()
    {
        var clip = new Clip { SourceIn = Time.FromTicks(100), SourceOut = Time.FromTicks(400), Start = Time.FromTicks(1000) };
        ClipTimeMap map = clip.TimeMap;

        Assert.True(map.IsIdentity);
        Assert.False(map.HasSpeed);
        Assert.Equal(Time.FromTicks(300), map.Duration);
        Assert.Equal(Time.FromTicks(150), map.Source(Time.FromTicks(1050)));
        Assert.Equal(Time.FromTicks(-900), map.ExtendedSource(Time.Zero));
        Assert.Equal(Time.FromTicks(1050), map.TimelineOf(Time.FromTicks(150)));
        Assert.Equal(1, map.RateAt(Time.Zero));
        Assert.Equal(clip.ToSourceTime(Time.FromTicks(5000)), map.ExtendedSource(Time.FromTicks(5000)));

        (ClipTimeMap left, ClipTimeMap right) = map.SplitAt(Time.FromTicks(1100));
        Assert.Equal((Time.FromTicks(100), Time.FromTicks(200)), (left.SourceIn, left.SourceOut));
        Assert.Equal((Time.FromTicks(200), Time.FromTicks(400), Time.Zero), (right.SourceIn, right.SourceOut, right.Offset));

        var copy = clip.Clone();
        right.ApplyTo(copy);
        Assert.Equal((Time.FromTicks(1100), Time.FromTicks(200), Time.FromTicks(400)), (copy.Start, copy.SourceIn, copy.SourceOut));
        Assert.Equal(Time.FromTicks(200), copy.Duration);
        Assert.Equal(ClipTimeMap.Create(Time.FromTicks(1000), Time.FromTicks(300), Time.FromTicks(100), null), map);
        Assert.Equal(map, ClipTimeMap.Fit(Time.FromTicks(1000), Time.FromTicks(100), Time.FromTicks(400), null));
    }

    [Fact]
    public void ApplyTo_WritesTimesAndOffset()
    {
        var clip = SpeedClip(new ClipSpeed { Rate = new Rational(3, 2) }, sourceIn: 0, duration: 1000);
        clip.SourceOut = Time.FromTicks(1500);
        (_, ClipTimeMap right) = clip.TimeMap.SplitAt(Time.FromTicks(10_333));

        right.ApplyTo(clip);

        Assert.Equal((Time.FromTicks(10_333), Time.FromTicks(667), Time.FromTicks(333)), (clip.Start, clip.Duration, clip.Speed!.Offset));
        Assert.Equal(right, clip.TimeMap);
        Assert.Throws<ArgumentException>(() => right.ApplyTo(new Clip()));
        Assert.Throws<ArgumentException>(() => new Clip { SourceOut = Time.FromTicks(5) }.TimeMap.ApplyTo(clip));
    }

    [Fact]
    public void Fit_IsTheLongestMapInsideTheSourceRange()
    {
        var random = new Random(17);
        for (int c = 0; c < Cases; c++)
        {
            ClipSpeed speed = RandomSpeed(random, freeze: false);
            Time sourceIn = Time.FromTicks(random.NextInt64(0, 1L << 40));
            Time sourceOut = sourceIn + Time.FromTicks(random.NextInt64(1_000, 1L << 36));
            ClipTimeMap map = ClipTimeMap.Fit(Time.Zero, sourceIn, sourceOut, speed);

            Assert.True(map.SourceIn >= sourceIn && map.SourceOut <= sourceOut, $"{map} sale de [{sourceIn}, {sourceOut})");
            Assert.Equal(speed.Reverse ? sourceOut : sourceIn, speed.Reverse ? map.SourceOut : map.SourceIn);
            if (Math.Abs(speed.Offset.Ticks) + map.Duration.Ticks < RetimeMath.MaxExtent)
            {
                ClipTimeMap longer = ClipTimeMap.Create(Time.Zero, map.Duration + Time.FromTicks(1), speed.Reverse ? sourceOut : sourceIn, speed);
                Assert.True(longer.SourceIn < sourceIn || longer.SourceOut > sourceOut);
            }
        }

        Assert.Throws<ArgumentException>(() => ClipTimeMap.Fit(Time.Zero, Time.FromTicks(5), Time.FromTicks(5), null));
        Assert.Throws<ArgumentException>(() =>
            ClipTimeMap.Fit(Time.Zero, Time.Zero, Time.FromTicks(10), new ClipSpeed { Rate = new Rational(2, 1), Offset = Time.FromTicks(RetimeMath.MaxExtent) }));
        ClipTimeMap capped = ClipTimeMap.Fit(Time.Zero, Time.Zero, Time.FromTicks(RetimeMath.MaxExtent * 2), new ClipSpeed());
        Assert.Equal(RetimeMath.MaxExtent, capped.Duration.Ticks);
    }

    [Fact]
    public void InvalidArguments_Throw()
    {
        ClipTimeMap map = ClipTimeMap.Create(Time.FromTicks(10), Time.FromTicks(10), Time.Zero, new ClipSpeed { Rate = new Rational(2, 1) });

        Assert.Throws<ArgumentOutOfRangeException>(() => map.SplitAt(Time.FromTicks(10)));
        Assert.Throws<ArgumentOutOfRangeException>(() => map.SplitAt(Time.FromTicks(20)));
        Assert.Throws<ArgumentOutOfRangeException>(() => map.TrimIn(Time.FromTicks(20)));
        Assert.Throws<ArgumentOutOfRangeException>(() => map.TrimOut(Time.FromTicks(10)));
        Assert.Throws<ArgumentOutOfRangeException>(() => ClipTimeMap.Create(Time.Zero, Time.Zero, Time.Zero, null));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ClipTimeMap.Create(Time.Zero, Time.FromTicks(RetimeMath.MaxExtent), Time.Zero, new ClipSpeed { Offset = Time.FromTicks(1) }));
        Assert.Throws<ArgumentOutOfRangeException>(() => ClipTimeMap.Create(Time.Zero, Time.FromTicks(1), Time.Zero,
            new ClipSpeed { Offset = Time.FromTicks(long.MinValue) }));
        Assert.Throws<ArgumentNullException>(() => ClipTimeMap.Of(null!));
        Assert.Throws<ArgumentNullException>(() => map.ApplyTo(null!));
    }

    [Fact]
    public void Equality_CoversEveryField()
    {
        ClipTimeMap a = ClipTimeMap.Create(Time.Zero, Time.FromTicks(100), Time.Zero, new ClipSpeed { Rate = new Rational(2, 1) });
        ClipTimeMap b = ClipTimeMap.Create(Time.Zero, Time.FromTicks(100), Time.Zero, new ClipSpeed { Rate = new Rational(2, 1) });

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.True(a.Equals((object)b));
        Assert.False(a.Equals(null));
        Assert.NotEqual(a, ClipTimeMap.Create(Time.Zero, Time.FromTicks(100), Time.Zero, new ClipSpeed { Rate = new Rational(3, 1) }));
        Assert.NotEqual(a, ClipTimeMap.Create(Time.Zero, Time.FromTicks(100), Time.FromTicks(200), new ClipSpeed { Rate = new Rational(2, 1), Reverse = true }));
        Assert.NotEqual(ClipTimeMap.Create(Time.Zero, Time.FromTicks(100), Time.Zero, null), ClipTimeMap.Create(Time.Zero, Time.FromTicks(100), Time.Zero, new ClipSpeed()));
    }

    internal static Clip SpeedClip(ClipSpeed speed, long sourceIn, long duration) => new()
    {
        Id = "c",
        MediaId = "m",
        Start = Time.FromTicks(10_000),
        SourceIn = Time.FromTicks(sourceIn),
        Speed = speed,
        Duration = Time.FromTicks(duration),
    };

    internal static ClipSpeed RandomSpeed(Random random, bool freeze = true)
    {
        var speed = new ClipSpeed();
        switch (random.Next(freeze ? 6 : 5))
        {
            case 0:
                break;
            case 1:
            case 2:
                long den = random.NextInt64(1, 2001);
                speed.Rate = new Rational(random.NextInt64(Math.Max(1, (den + 9) / 10), (den * 100) + 1), den);
                break;
            case 3:
            case 4:
                speed.Curve = RetimeMathTests.RandomCurve(random);
                break;
            default:
                speed.Freeze = true;
                break;
        }

        speed.Reverse = !speed.Freeze && random.Next(3) == 0;
        speed.Offset = Time.FromTicks(random.Next(3) == 0 ? 0 : random.NextInt64(-(1L << 40), 1L << 40));
        return speed;
    }

    private static ClipTimeMap RandomMap(Random random)
    {
        ClipSpeed speed = RandomSpeed(random);
        long duration = random.Next(3) switch
        {
            0 => random.NextInt64(1, 100),
            1 => random.NextInt64(1, 1L << 40),
            _ => (long)Math.Pow(2, random.NextDouble() * 40) + 1,
        };
        return ClipTimeMap.Create(Time.FromTicks(random.NextInt64(0, 1L << 50)), Time.FromTicks(duration),
            Time.FromTicks(random.NextInt64(1L << 52, 1L << 53)), speed);
    }

    private static Time RandomInside(Random random, ClipTimeMap map) => map.Start + Time.FromTicks(random.Next(4) switch
    {
        0 => 0,
        1 => map.Duration.Ticks - 1,
        _ => random.NextInt64(0, map.Duration.Ticks),
    });

    private static void InRange(Time value, Time low, Time high) =>
        Assert.True(value >= low && value <= high, $"{value} fuera de [{low}, {high}]");

    private static bool Reaches(ClipTimeMap map, Time shown, Time wanted) => map.Reverse ? shown <= wanted : shown >= wanted;
}
