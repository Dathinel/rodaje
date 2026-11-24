using Rodaje.Core.Timing;
using Rodaje.Engine.Presentation;

namespace Rodaje.Engine.Tests;

public sealed class PresentationBoundaryInvariantsTests
{
    [Theory]
    [InlineData(59.999, "0:59.999")]
    [InlineData(60, "1:00.000")]
    [InlineData(60.001, "1:00.001")]
    [InlineData(3599.999, "59:59.999")]
    [InlineData(3600, "1:00:00.000")]
    [InlineData(3600.001, "1:00:00.001")]
    [InlineData(86399.999, "23:59:59.999")]
    [InlineData(86400, "24:00:00.000")]
    [InlineData(90061.125, "25:01:01.125")]
    [InlineData(360000, "100:00:00.000")]
    public void Minute_hour_and_day_boundaries_keep_the_exact_milliseconds(double seconds, string expected)
    {
        Assert.Equal(expected, Timecode.Format(Time.FromSeconds(seconds)));
    }
}
