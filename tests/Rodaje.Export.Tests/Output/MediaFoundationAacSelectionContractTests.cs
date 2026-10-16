using Rodaje.Export;

namespace Rodaje.Export.Tests.Output;

public sealed class MediaFoundationAacSelectionContractTests
{
    [Theory]
    [InlineData(64, 96)]
    [InlineData(96, 96)]
    [InlineData(112, 96)]
    [InlineData(144, 128)]
    [InlineData(176, 160)]
    [InlineData(200, 192)]
    [InlineData(320, 192)]
    public void Chooses_the_nearest_supported_aac_bitrate(int requestedKbps, int expectedKbps)
    {
        Assert.Equal(expectedKbps, FfmpegArguments.NearestMediaFoundationAac(requestedKbps));
    }
}
