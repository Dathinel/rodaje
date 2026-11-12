using Rodaje.Media.Gpu;

namespace Rodaje.Media.Tests;

[System.Runtime.Versioning.SupportedOSPlatform("windows10.0")]
public sealed class GpuOutputBitsTests
{
    [Fact]
    public void WarpNoTieneMonitores_YDaOchoBits()
    {
        using GpuDevice gpu = GpuDevice.CreateWarp();
        Assert.Equal(8, gpu.MaxOutputBitsPerColor());
    }
}
