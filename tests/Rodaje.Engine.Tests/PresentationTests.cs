using Rodaje.Core.Timing;
using Rodaje.Engine.Presentation;

namespace Rodaje.Engine.Tests;

public sealed class PresentationTests
{
    [Theory]
    [InlineData(0L, "0:00.000")]
    [InlineData(1_500L, "0:01.500")]
    [InlineData(61_001L, "1:01.001")]
    [InlineData(3_723_456L, "1:02:03.456")]
    [InlineData(-2_000L, "-0:02.000")]
    public void Timecode_formats_minutes_and_hours(long milliseconds, string expected) =>
        Assert.Equal(expected, Timecode.Format(Time.FromTicks(milliseconds * (Time.TicksPerSecond / 1000))));
}
