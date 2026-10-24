using Rodaje.App.Services.Settings;

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

    [Fact]
    public void The_default_is_auto_and_an_unknown_value_falls_back_to_it()
    {
        var settings = new AppSettings();
        Assert.Equal(TenBitViewer.Auto, settings.TenBitViewer);
        settings.TenBitViewer = (TenBitViewer)7;
        settings.Normalize();
        Assert.Equal(TenBitViewer.Auto, settings.TenBitViewer);
    }
}
