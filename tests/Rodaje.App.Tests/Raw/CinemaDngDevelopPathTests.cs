using Rodaje.App.Views.Raw;

namespace Rodaje.App.Tests.Raw;

public sealed class CinemaDngDevelopPathTests
{
    [Theory]
    [InlineData(false, false, null, "Cpu", true)]
    [InlineData(false, true, true, "Cpu", true)]
    [InlineData(true, false, null, "Cpu", true)]
    [InlineData(true, false, true, "Cpu", true)]
    [InlineData(true, true, false, "Cpu", true)]
    [InlineData(true, true, null, "PendingFrame", true)]
    [InlineData(true, true, true, "Gpu", false)]
    public void PathAndQualityReflectModuleAndFrame(bool hardware, bool mosaic, bool? bayer, string path, bool quality)
    {
        CinemaDngDevelopChoice choice = CinemaDngDevelopChoice.Decide(hardware, mosaic, bayer);

        Assert.Equal(path, choice.Path.ToString());
        Assert.Equal(quality, choice.OfferQuality);
        if (choice.Path == CinemaDngDevelopPath.PendingFrame)
        {
            Assert.Contains("GPU si el módulo y el cuadro lo permiten; si no, CPU", choice.Description, StringComparison.Ordinal);
        }
    }
}
