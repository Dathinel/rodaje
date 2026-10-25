using Rodaje.App.Services.Settings;
using Rodaje.App.Views.Player;
using Rodaje.Media.Gpu;
using Rodaje.Render;

namespace Rodaje.App.Tests.Player;

public class ViewerBitDepthTests
{
    [Theory]
    [InlineData(TenBitViewer.Auto, 8, false)]
    [InlineData(TenBitViewer.Auto, 10, true)]
    [InlineData(TenBitViewer.Auto, 12, true)]
    [InlineData(TenBitViewer.On, 8, true)]
    [InlineData(TenBitViewer.Off, 10, false)]
    public void Auto_follows_the_monitor_and_the_switch_overrides_it(TenBitViewer setting, int bitsPerColor, bool tenBits) =>
        Assert.Equal(tenBits, ViewerBitDepth.UsesTenBits(setting, bitsPerColor));

    [Theory]
    [InlineData(HdrViewer.Auto, false, false)]
    [InlineData(HdrViewer.Auto, true, true)]
    [InlineData(HdrViewer.On, false, true)]
    [InlineData(HdrViewer.Off, true, false)]
    public void The_hdr_viewer_follows_the_windows_hdr_mode_and_the_switch_overrides_it(HdrViewer setting, bool monitorInHdr, bool scRgb) =>
        Assert.Equal(scRgb, ViewerBitDepth.UsesScRgb(setting, monitorInHdr));

    [Fact]
    public void The_default_is_auto_and_an_unknown_value_falls_back_to_it()
    {
        var settings = new AppSettings();
        Assert.Equal(TenBitViewer.Auto, settings.TenBitViewer);
        Assert.Equal(HdrViewer.Auto, settings.HdrViewer);
        settings.TenBitViewer = (TenBitViewer)7;
        settings.HdrViewer = (HdrViewer)7;
        settings.Normalize();
        Assert.Equal(TenBitViewer.Auto, settings.TenBitViewer);
        Assert.Equal(HdrViewer.Auto, settings.HdrViewer);
    }

    /// <summary>WARP no tiene monitores: no hay HDR, y una cadena scRGB forzada nace en media precisión (o cae a SDR si el adaptador no la admite) y presenta.</summary>
    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows10.0")]
    public void Warp_has_no_hdr_output_and_a_forced_scrgb_chain_is_half_float_or_falls_back()
    {
        using GpuDevice gpu = GpuDevice.CreateWarp();
        Assert.False(CompositionSwapChain.HasHdrOutput(gpu));
        using var presenter = new Presenter(gpu);
        using var sdr = new CompositionSwapChain(gpu, 64, 32, 1, 1, tenBits: true);
        Assert.True(sdr.TenBits);
        Assert.False(sdr.ScRgb);
        sdr.Render(presenter, null, waitForVsync: false);

        using var hdr = new CompositionSwapChain(gpu, 64, 32, 1, 1, tenBits: true, scRgb: true);
        Assert.False(hdr.TenBits);
        hdr.Render(presenter, null, waitForVsync: false);
        // WARP no admite el espacio scRGB en una cadena de composición: cae a SDR sin fallar; con un adaptador real queda en scRGB.
        Assert.False(hdr.ScRgb && hdr.TenBits);
    }
}
