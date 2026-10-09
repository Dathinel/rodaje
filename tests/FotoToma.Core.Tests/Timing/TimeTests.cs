using FotoToma.Core.Timing;

namespace FotoToma.Core.Tests.Timing;

public class TimeTests
{
    private const long T = Time.TicksPerSecond;

    // Tasas que, según la documentación de Time, caen en ticks exactos: base y variante NTSC (×1000/1001)
    public static TheoryData<long, long> ExactFrameRates()
    {
        var data = new TheoryData<long, long>();
        foreach (long fps in new long[] { 24, 25, 30, 48, 50, 60, 90, 100, 120 })
        {
            data.Add(fps, 1);
            data.Add(fps * 1000, 1001);
        }

        return data;
    }

    public static TheoryData<int> ExactSampleRates() =>
        new() { 8000, 16000, 22050, 24000, 32000, 44100, 48000, 88200, 96000, 192000 };

    [Theory]
    [MemberData(nameof(ExactFrameRates))]
    public void TicksPerSecond_IsDivisibleByFrameDuration(long num, long den)
    {
        var rate = new Rational(num, den);
        // Un cuadro dura den/num segundos = T·den/num ticks; tiene que ser entero
        Assert.Equal(0, (T * rate.Den) % rate.Num);
        long frameTicks = T * rate.Den / rate.Num;

        foreach (long n in new long[] { 0, 1, 2, 3, 7, 1000, 1001, 30_000, 86_400L * 120, -1, -1001 })
        {
            Time start = Time.FromFrames(n, rate);
            Assert.Equal(n * frameTicks, start.Ticks);
            Assert.Equal(n, start.ToFrames(rate));
            // El último tick del cuadro sigue siendo el mismo cuadro y el siguiente es el que sigue
            Assert.Equal(n, Time.FromTicks(start.Ticks + frameTicks - 1).ToFrames(rate));
            Assert.Equal(n + 1, Time.FromTicks(start.Ticks + frameTicks).ToFrames(rate));
        }

        // Sumar k veces la duración de un cuadro es exactamente FromFrames(n·k)
        Time one = Time.FromFrames(1, rate);
        Time accumulated = Time.Zero;
        for (int k = 1; k <= 10_000; k++)
        {
            accumulated += one;
        }

        Assert.Equal(Time.FromFrames(10_000, rate), accumulated);
        Assert.Equal(Time.FromFrames(7 * 13, rate), Time.FromTicks(Time.FromFrames(7, rate).Ticks * 13));
    }

    [Theory]
    [MemberData(nameof(ExactSampleRates))]
    public void TicksPerSecond_IsDivisibleBySamplePeriod(int sampleRate)
    {
        Assert.Equal(0, T % sampleRate);
        long sampleTicks = T / sampleRate;

        foreach (long s in new long[] { 0, 1, 2, 441, sampleRate, sampleRate * 3600L, -1, -sampleRate })
        {
            Time start = Time.FromSamples(s, sampleRate);
            Assert.Equal(s * sampleTicks, start.Ticks);
            Assert.Equal(s, start.ToSamples(sampleRate));
            Assert.Equal(s, Time.FromTicks(start.Ticks + sampleTicks - 1).ToSamples(sampleRate));
        }

        Assert.Equal(Time.FromSeconds(1), Time.FromSamples(sampleRate, sampleRate));
    }

    [Fact]
    public void FromFrames_RoundsUpWhenFrameIsNotWholeTicks()
    {
        // 7 cuadros cada 705 600 001 s: un cuadro no dura un número entero de ticks
        var rate = new Rational(T + 1, 1);
        Time one = Time.FromFrames(1, rate);
        Assert.Equal(1, one.Ticks);
        Assert.Equal(1, one.ToFrames(rate));
        Assert.Equal(0, Time.FromFrames(0, rate).Ticks);

        // Con 7 fps 1/7 s = 100 800 000 ticks exactos, pero 1/11 s no lo es
        var eleven = new Rational(11, 1);
        Assert.Equal(64_145_455, Time.FromFrames(1, eleven).Ticks); // ceil(705 600 000 / 11)
        Assert.Equal(1, Time.FromFrames(1, eleven).ToFrames(eleven));
        Assert.Equal(0, Time.FromTicks(64_145_454).ToFrames(eleven));
        Assert.Equal(-64_145_454, Time.FromFrames(-1, eleven).Ticks); // ceil hacia +∞ también en negativos
        Assert.Equal(-1, Time.FromFrames(-1, eleven).ToFrames(eleven));
    }

    [Fact]
    public void ToFrames_FloorsInsideFrameAndForNegatives()
    {
        long frame = T / 30;
        Assert.Equal(0, Time.FromTicks(0).ToFrames(Rational.Fps30));
        Assert.Equal(0, Time.FromTicks(frame / 2).ToFrames(Rational.Fps30));
        Assert.Equal(0, Time.FromTicks(frame - 1).ToFrames(Rational.Fps30));
        Assert.Equal(1, Time.FromTicks(frame).ToFrames(Rational.Fps30));
        Assert.Equal(-1, Time.FromTicks(-1).ToFrames(Rational.Fps30));
        Assert.Equal(-1, Time.FromTicks(-frame).ToFrames(Rational.Fps30));
        Assert.Equal(-2, Time.FromTicks(-frame - 1).ToFrames(Rational.Fps30));
    }

    [Fact]
    public void SnapToFrame_ChoosesNearestAndTiesGoToLaterFrame()
    {
        Rational rate = Rational.Fps30;
        long frame = T / 30; // 23 520 000
        long half = frame / 2;

        Assert.Equal(Time.Zero, Time.Zero.SnapToFrame(rate));
        Assert.Equal(Time.Zero, Time.FromTicks(half - 1).SnapToFrame(rate));
        // Empate exacto a mitad de cuadro: se elige el cuadro siguiente
        Assert.Equal(Time.FromFrames(1, rate), Time.FromTicks(half).SnapToFrame(rate));
        Assert.Equal(Time.FromFrames(1, rate), Time.FromTicks(half + 1).SnapToFrame(rate));
        Assert.Equal(Time.FromFrames(1, rate), Time.FromTicks(frame).SnapToFrame(rate));
        Assert.Equal(Time.FromFrames(5, rate), Time.FromTicks((5 * frame) + 3).SnapToFrame(rate));

        // Negativos: el empate también va hacia el cuadro posterior (hacia +∞)
        Assert.Equal(Time.Zero, Time.FromTicks(-1).SnapToFrame(rate));
        Assert.Equal(Time.Zero, Time.FromTicks(-half).SnapToFrame(rate));
        Assert.Equal(Time.FromFrames(-1, rate), Time.FromTicks(-half - 1).SnapToFrame(rate));
    }

    [Fact]
    public void SnapToFrame_WorksWithNtscRate()
    {
        Rational rate = Rational.Ntsc30;
        Time f100 = Time.FromFrames(100, rate);
        long frame = Time.FromFrames(1, rate).Ticks;
        Assert.Equal(f100, (f100 + Time.FromTicks((frame / 2) - 1)).SnapToFrame(rate));
        Assert.Equal(Time.FromFrames(101, rate), (f100 + Time.FromTicks(frame / 2)).SnapToFrame(rate));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Samples_RejectNonPositiveRate(int sampleRate)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Time.FromSamples(1, sampleRate));
        Assert.Throws<ArgumentOutOfRangeException>(() => Time.FromTicks(1).ToSamples(sampleRate));
    }

    [Fact]
    public void FromSamples_RoundsUpAndToSamplesFloors()
    {
        // 44 100 Hz entra exacto; 44 101 Hz no
        Assert.Equal(16_000, Time.FromSamples(1, 44_100).Ticks);
        Assert.Equal(16_000, Time.FromSamples(1, 44_101).Ticks); // ceil(705 600 000 / 44 101) = ceil(15 999,6)
        Assert.Equal(1, Time.FromSamples(1, 44_101).ToSamples(44_101));
        Assert.Equal(0, Time.FromTicks(15_999).ToSamples(44_101));
        Assert.Equal(-1, Time.FromTicks(-1).ToSamples(48_000));
    }

    [Fact]
    public void FromSeconds_RoundsToNearestTick()
    {
        Assert.Equal(T, Time.FromSeconds(1).Ticks);
        Assert.Equal(T * 3 / 2, Time.FromSeconds(1.5).Ticks);
        Assert.Equal(-T / 2, Time.FromSeconds(-0.5).Ticks);
        Assert.Equal(0, Time.FromSeconds(0.4 / T).Ticks);
        Assert.Equal(1, Time.FromSeconds(0.6 / T).Ticks);
        Assert.Equal(T * 3600, Time.FromSeconds(3600).Ticks);
    }

    [Fact]
    public void ToSeconds_DividesByTicksPerSecond()
    {
        Assert.Equal(0.0, Time.Zero.ToSeconds());
        Assert.Equal(1.5, Time.FromTicks(T * 3 / 2).ToSeconds());
        Assert.Equal(-2.0, Time.FromTicks(-2 * T).ToSeconds());
        Assert.Equal(12.345, Time.FromSeconds(12.345).ToSeconds(), 9);
    }

    [Fact]
    public void Constructors_AndZero()
    {
        Assert.Equal(0, Time.Zero.Ticks);
        Assert.Equal(default, Time.Zero);
        Assert.Equal(new Time(42), Time.FromTicks(42));
        Assert.Equal(42, new Time(42).Ticks);
    }

    [Fact]
    public void ArithmeticOperators_AndNamedEquivalents()
    {
        var a = Time.FromTicks(100);
        var b = Time.FromTicks(30);

        Assert.Equal(130, (a + b).Ticks);
        Assert.Equal(70, (a - b).Ticks);
        Assert.Equal(-100, (-a).Ticks);
        Assert.Equal(a + b, a.Add(b));
        Assert.Equal(a - b, a.Subtract(b));
        Assert.Equal(-a, a.Negate());
        Assert.Equal(a, -(-a));
    }

    [Fact]
    public void ArithmeticOverflow_Throws()
    {
        var max = Time.FromTicks(long.MaxValue);
        var min = Time.FromTicks(long.MinValue);
        var one = Time.FromTicks(1);

        Assert.Throws<OverflowException>(() => max + one);
        Assert.Throws<OverflowException>(() => min - one);
        Assert.Throws<OverflowException>(() => min + (-one));
        Assert.Throws<OverflowException>(() => -min);
        Assert.Throws<OverflowException>(() => max.Add(one));
        Assert.Throws<OverflowException>(() => min.Subtract(one));
        Assert.Throws<OverflowException>(() => min.Negate());
        Assert.Throws<OverflowException>(() => Time.Zero - min);
        Assert.Equal(long.MinValue + 1, (-max).Ticks);
    }

    [Fact]
    public void Conversions_ThatDoNotFitInLong_Throw()
    {
        Assert.Throws<OverflowException>(() => Time.FromFrames(long.MaxValue, Rational.Fps30));
        Assert.Throws<OverflowException>(() => Time.FromFrames(long.MinValue, Rational.Fps30));
        Assert.Throws<OverflowException>(() => Time.FromSamples(long.MaxValue, 48_000));
        Assert.Throws<OverflowException>(() => Time.FromTicks(long.MaxValue).ToFrames(new Rational(T * 10, 1)));
        Assert.Throws<OverflowException>(() => Time.FromTicks(long.MaxValue).ToSamples(int.MaxValue));
    }

    [Fact]
    public void ComparisonOperators()
    {
        var a = Time.FromTicks(1);
        var b = Time.FromTicks(2);
        var a2 = Time.FromTicks(1);

        Assert.True(a < b);
        Assert.False(b < a);
        Assert.False(a < a2);
        Assert.True(b > a);
        Assert.False(a > b);
        Assert.False(a > a2);
        Assert.True(a <= a2);
        Assert.True(a <= b);
        Assert.False(b <= a);
        Assert.True(a >= a2);
        Assert.True(b >= a);
        Assert.False(a >= b);
        Assert.True(a == a2);
        Assert.True(a != b);
    }

    [Fact]
    public void CompareTo_OrdersByTicks()
    {
        Assert.True(Time.FromTicks(-5).CompareTo(Time.FromTicks(3)) < 0);
        Assert.True(Time.FromTicks(3).CompareTo(Time.FromTicks(-5)) > 0);
        Assert.Equal(0, Time.FromTicks(3).CompareTo(Time.FromTicks(3)));

        var list = new List<Time> { Time.FromTicks(3), Time.FromTicks(-1), Time.FromTicks(2), Time.Zero };
        list.Sort();
        Assert.Equal([Time.FromTicks(-1), Time.Zero, Time.FromTicks(2), Time.FromTicks(3)], list);
        Assert.Equal(Time.FromTicks(3), list.Max());
    }

    [Fact]
    public void MinAndMax()
    {
        var a = Time.FromTicks(-10);
        var b = Time.FromTicks(20);

        Assert.Equal(a, Time.Min(a, b));
        Assert.Equal(a, Time.Min(b, a));
        Assert.Equal(b, Time.Max(a, b));
        Assert.Equal(b, Time.Max(b, a));
        Assert.Equal(a, Time.Min(a, a));
        Assert.Equal(b, Time.Max(b, b));
    }

    [Theory]
    [InlineData(0L, "00:00:00.000")]
    [InlineData(705_600L, "00:00:00.001")]
    [InlineData(705_599L, "00:00:00.000")]
    [InlineData(1L, "00:00:00.000")]
    [InlineData(-1L, "-00:00:00.000")]
    [InlineData(705_600_000L, "00:00:01.000")]
    [InlineData(-1_058_400_000L, "-00:00:01.500")]
    [InlineData(705_600L * 3_723_456, "01:02:03.456")]
    [InlineData(-705_600L * 3_723_456, "-01:02:03.456")]
    [InlineData(705_600_000L * 3600 * 100, "100:00:00.000")]
    [InlineData(705_600L * 999 + 705_599, "00:00:00.999")]
    [InlineData(705_600_000L * 59 + 705_600L * 999, "00:00:59.999")]
    [InlineData(705_600_000L * 3599, "00:59:59.000")]
    public void ToString_FormatsHoursMinutesSecondsMillis(long ticks, string expected)
    {
        Assert.Equal(expected, Time.FromTicks(ticks).ToString());
    }

    [Fact]
    public void ToString_HandlesExtremes()
    {
        // long.MaxValue / 705 600 = 13 071 672 387 832 ms
        Assert.Equal("3631020:06:27.832", Time.FromTicks(long.MaxValue).ToString());
        Assert.Equal("-3631020:06:27.832", Time.FromTicks(long.MinValue).ToString());
    }

    [Fact]
    public void ToString_IgnoresCurrentCulture()
    {
        System.Globalization.CultureInfo previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("es-CO");
            Assert.Equal("00:00:01.500", Time.FromSeconds(1.5).ToString());
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }
}
