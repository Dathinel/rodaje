using Rodaje.Media.Gpu;

namespace Rodaje.Media.Tests.Gpu;

/// <summary>
/// Un equipo sin Direct3D 11 por hardware (GPU de nivel 10, controlador roto): antes el visor no abría («Hace falta una
/// GPU con Direct3D 11») y exportar fallaba; ahora van por WARP, lentos pero funcionan.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows10.0")]
public sealed class NoHardwareFallbackTests
{
    [Fact]
    [Trait("Categoria", "SoloWindows")]
    public void SinHardware_CreaWarp()
    {
        using GpuDevice gpu = GpuDevice.Create(() => null);
        Assert.True(gpu.IsSoftware);
        Assert.False(gpu.SupportsHardwareDecoding);
    }
}
