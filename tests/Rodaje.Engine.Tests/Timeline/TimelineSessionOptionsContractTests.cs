using Rodaje.Engine.Timeline;

namespace Rodaje.Engine.Tests.Timeline;

public sealed class TimelineSessionOptionsContractTests
{
    [Theory]
    [InlineData(0, 1536L * 1024 * 1024)]
    [InlineData(4L * 1024 * 1024 * 1024, 1536L * 1024 * 1024)]
    [InlineData(8L * 1024 * 1024 * 1024, 3L * 1024 * 1024 * 1024)]
    [InlineData(100L * 1024 * 1024 * 1024, 4L * 1024 * 1024 * 1024)]
    public void Vram_budget_uses_default_ratio_and_limits(long dedicatedBytes, long expectedBytes)
    {
        Assert.Equal(expectedBytes, new TimelineSessionOptions().ResolveVramBudget(dedicatedBytes));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Explicit_vram_budget_must_be_positive(long budget)
    {
        TimelineSessionOptions options = new() { VramBudgetBytes = budget };

        Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
    }

    [Fact]
    public void Frame_cache_must_fit_prefetch_preroll_and_two_frames_of_margin()
    {
        TimelineSessionOptions valid = new() { PrefetchFrames = 10, PreRollFrames = 4, MaxFramesPerSource = 16 };
        valid.Validate();

        TimelineSessionOptions tooSmall = valid with { MaxFramesPerSource = 15 };
        ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(tooSmall.Validate);
        Assert.Equal("MaxFramesPerSource", error.ParamName);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(11)]
    public void Pre_roll_is_bounded_to_ten_seconds(int seconds)
    {
        TimelineSessionOptions options = new() { PreRoll = TimeSpan.FromSeconds(seconds) };

        Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
    }
}
