namespace Rodaje.Module.Raw.Tests;

public sealed class BayerPatternTests
{
    [Theory]
    [InlineData(0x94949494u, "RGBG", "RGGB")]
    [InlineData(0x16161616u, "RGBG", "BGGR")]
    [InlineData(0x61616161u, "RGBG", "GRBG")]
    [InlineData(0x49494949u, "RGBG", "GBRG")]
    [InlineData(0xB4B4B4B4u, "RGBG", "RGGB")]
    [InlineData(0xB4949494u, "RGBG", "RGGB")]
    [InlineData(0x94949494u, "BGRG", "BGGR")]
    public void RepeatingRgbPatternsAreBayer(uint filters, string description, string expected) =>
        Assert.Equal(expected, RawDeveloper.BayerPattern(filters, 3, System.Text.Encoding.ASCII.GetBytes(description)));

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void APhaseChangeInAnyLaterRowIsNotBayer(int row)
    {
        uint filters = 0x94949494u ^ (3u << (row * 4));

        Assert.Null(RawDeveloper.BayerPattern(filters, 3, "RGBG"u8));
    }

    [Theory]
    [InlineData(0u, 3, "RGBG")]
    [InlineData(1u, 3, "RGBG")]
    [InlineData(9u, 3, "RGBG")]
    [InlineData(0x94949494u, 4, "RGBG")]
    [InlineData(0x94949494u, 3, "CMYG")]
    [InlineData(0x14141414u, 3, "RGBG")]
    public void UnsupportedPatternsAreNotBayer(uint filters, int colors, string description) =>
        Assert.Null(RawDeveloper.BayerPattern(filters, colors, System.Text.Encoding.ASCII.GetBytes(description)));
}
