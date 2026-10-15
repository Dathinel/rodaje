using Rodaje.Media.Proxies;

namespace Rodaje.Media.Tests.Proxies;

public sealed class FfmpegProgressParserContractTests
{
    [Fact]
    public void Malformed_fields_leave_the_last_valid_values_intact()
    {
        var parser = new FfmpegProgressParser();
        Assert.False(parser.Feed("frame=42", out _));
        Assert.False(parser.Feed("out_time_us=900000", out _));
        Assert.False(parser.Feed("frame=not-a-number", out _));
        Assert.False(parser.Feed("out_time_us=N/A", out _));

        Assert.True(parser.Feed("progress=continue", out FfmpegProgressSnapshot snapshot));
        Assert.Equal(new FfmpegProgressSnapshot(42, 900_000, false), snapshot);
    }

    [Fact]
    public void A_new_block_keeps_omitted_counters_and_only_end_marks_completion()
    {
        var parser = new FfmpegProgressParser();
        parser.Feed("frame=24", out _);
        parser.Feed("out_time_us=1200000", out _);
        Assert.True(parser.Feed("progress=continue", out _));

        Assert.False(parser.Feed("frame=48", out _));
        Assert.Equal(new FfmpegProgressSnapshot(24, 1_200_000, false), parser.Last);
        Assert.True(parser.Feed("progress=end", out FfmpegProgressSnapshot ended));
        Assert.Equal(new FfmpegProgressSnapshot(48, 1_200_000, true), ended);
    }
}
