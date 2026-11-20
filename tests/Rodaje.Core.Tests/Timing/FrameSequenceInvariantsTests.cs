using Rodaje.Core.Timing;

namespace Rodaje.Core.Tests.Timing;

public sealed class FrameSequenceInvariantsTests
{
    [Theory]
    [InlineData(24000, 1001)]
    [InlineData(24, 1)]
    [InlineData(25, 1)]
    [InlineData(30000, 1001)]
    [InlineData(30, 1)]
    [InlineData(50, 1)]
    [InlineData(60000, 1001)]
    [InlineData(60, 1)]
    public void First_two_hundred_thousand_frames_match_integer_oracle_without_drift(long numerator, long denominator)
    {
        var rate = new Rational(numerator, denominator);
        Time accumulated = Time.Zero;
        for (long frame = 0; frame < 200_000; frame++)
        {
            long expected = (long)(((Int128)frame * Time.TicksPerSecond * denominator + numerator - 1) / numerator);
            Time actual = Time.FromFrames(frame, rate);
            Assert.True(actual.Ticks == expected && actual.ToFrames(rate) == frame && accumulated == actual,
                $"Tasa {numerator}/{denominator}, fotograma {frame}: esperado {expected}, obtenido {actual.Ticks}, acumulado {accumulated.Ticks}.");
            Assert.Equal(frame - 1, (actual - Time.FromTicks(1)).ToFrames(rate));
            Assert.Equal(actual, actual.SnapToFrame(rate));
            accumulated += Time.FromFrames(frame + 1, rate) - actual;
        }
    }

    [Theory]
    [InlineData(86400, "24:00:00.000")]
    [InlineData(90061.125, "25:01:01.125")]
    [InlineData(360000, "100:00:00.000")]
    [InlineData(604800, "168:00:00.000")]
    public void Durations_beyond_one_day_keep_the_total_hours(double seconds, string expected)
    {
        Time duration = Time.FromSeconds(seconds);
        Assert.Equal(expected, duration.ToString());
        Assert.True(TimeInput.TryParse(expected, Rational.Fps60, Time.Zero, out Time parsed));
        Assert.Equal(duration.SnapToFrame(Rational.Fps60), parsed);
    }
}
