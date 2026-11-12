using Rodaje.App.Shell;

namespace Rodaje.App.Tests.Shell;

public sealed class GpuNameFormatterTests
{
    [Theory]
    [InlineData("NVIDIA GeForce RTX 3050 Laptop GPU", "RTX 3050 Laptop")]
    [InlineData("NVIDIA GeForce RTX 3050 Laptop GPU (nivel 12_1, D3D11VA)", "RTX 3050 Laptop")]
    [InlineData("NVIDIA GeForce RTX 4090", "RTX 4090")]
    [InlineData("NVIDIA GeForce GTX 1660 Ti", "GTX 1660 Ti")]
    [InlineData("NVIDIA RTX A4000", "RTX A4000")]
    [InlineData("NVIDIA Quadro P1000", "Quadro P1000")]
    public void Abbreviate_NvidiaDescriptions_ProducesLegibleShortName(string raw, string expected)
    {
        string actual = GpuNameFormatter.Abbreviate(raw);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("AMD Radeon RX 6700 XT", "RX 6700 XT")]
    [InlineData("AMD Radeon RX 7900 XTX (nivel 12_1, D3D11VA)", "RX 7900 XTX")]
    [InlineData("AMD Radeon(TM) Graphics", "Radeon Graphics")]
    [InlineData("AMD Radeon PRO W6600", "Radeon PRO W6600")]
    public void Abbreviate_AmdDescriptions_ProducesLegibleShortName(string raw, string expected)
    {
        string actual = GpuNameFormatter.Abbreviate(raw);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("Intel(R) Arc(TM) A770 Graphics", "Arc A770")]
    [InlineData("Intel(R) Iris(R) Xe Graphics", "Iris Xe")]
    [InlineData("Intel(R) UHD Graphics 770", "UHD Graphics 770")]
    public void Abbreviate_IntelDescriptions_ProducesLegibleShortName(string raw, string expected)
    {
        string actual = GpuNameFormatter.Abbreviate(raw);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Abbreviate_MicrosoftBasicRenderDriver_ProducesWarp()
    {
        string actual = GpuNameFormatter.Abbreviate("Microsoft Basic Render Driver");

        Assert.Equal("WARP", actual);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Abbreviate_NullOrEmpty_ReturnsEmptyString(string? raw)
    {
        string actual = GpuNameFormatter.Abbreviate(raw);

        Assert.Equal("", actual);
    }
}
