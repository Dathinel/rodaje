namespace Rodaje.Packages.Tests.Content;

using Rodaje.Packages.Content;
using Xunit;

public sealed class SafeZonePresetTests
{
    [Theory]
    [InlineData(9, 16, 1080, 1920)]
    [InlineData(9, 16, 540, 960)]
    [InlineData(16, 9, 1920, 1080)]
    [InlineData(1, 1, 1000, 1000)]
    public void Fits_WithExactAspectRatio_ReturnsTrue(int aspectWidth, int aspectHeight, int width, int height)
    {
        var preset = CreatePreset(aspectWidth, aspectHeight);

        Assert.True(preset.Fits(width, height));
    }

    [Theory]
    [InlineData(100, 100, 1005, 1000)]
    [InlineData(100, 100, 995, 1000)]
    [InlineData(9, 16, 1085, 1920)]
    [InlineData(9, 16, 1075, 1920)]
    public void Fits_WithinOnePercentMargin_ReturnsTrue(int aspectWidth, int aspectHeight, int width, int height)
    {
        var preset = CreatePreset(aspectWidth, aspectHeight);

        Assert.True(preset.Fits(width, height));
    }

    [Theory]
    [InlineData(100, 100, 102, 100)]
    [InlineData(100, 100, 98, 100)]
    [InlineData(9, 16, 1100, 1920)]
    [InlineData(9, 16, 1060, 1920)]
    [InlineData(9, 16, 1920, 1080)]
    public void Fits_OutsideOnePercentMargin_ReturnsFalse(int aspectWidth, int aspectHeight, int width, int height)
    {
        var preset = CreatePreset(aspectWidth, aspectHeight);

        Assert.False(preset.Fits(width, height));
    }

    [Theory]
    [InlineData(0, 16)]
    [InlineData(9, 0)]
    [InlineData(0, 0)]
    [InlineData(-1, 16)]
    [InlineData(9, -1)]
    [InlineData(-1, -1)]
    public void Fits_WhenAspectWidthOrAspectHeightIsZeroOrNegative_ReturnsTrue(int aspectWidth, int aspectHeight)
    {
        var preset = CreatePreset(aspectWidth, aspectHeight);

        Assert.True(preset.Fits(1920, 1080));
    }

    [Theory]
    [InlineData(0, 1920)]
    [InlineData(1080, 0)]
    [InlineData(0, 0)]
    [InlineData(-1080, 1920)]
    [InlineData(1080, -1920)]
    [InlineData(-1, -1)]
    public void Fits_WhenWidthOrHeightIsZeroOrNegative_ReturnsTrue(int width, int height)
    {
        var preset = CreatePreset(9, 16);

        Assert.True(preset.Fits(width, height));
    }

    [Fact]
    public void AspectText_WithNineSixteenAspect_ReturnsFormattedString()
    {
        var preset = CreatePreset(9, 16);

        Assert.Equal("9:16", preset.AspectText);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 16)]
    [InlineData(9, 0)]
    [InlineData(-1, 16)]
    [InlineData(9, -1)]
    [InlineData(-1, -1)]
    public void AspectText_WhenAspectWidthOrAspectHeightIsZeroOrNegative_ReturnsEmptyString(int aspectWidth, int aspectHeight)
    {
        var preset = CreatePreset(aspectWidth, aspectHeight);

        Assert.Empty(preset.AspectText);
    }

    private static SafeZonePreset CreatePreset(int aspectWidth, int aspectHeight) =>
        new("preset-id", "Preset Name", aspectWidth, aspectHeight, [], false);
}
