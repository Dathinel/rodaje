using Rodaje.App.Views.Player;

namespace Rodaje.App.Tests.Player;

public class FullScreenTests
{
    [Fact]
    public void LosControlesAparecenAlMoverYSeVanSolos()
    {
        var overlay = new OverlayAutoHide(TimeSpan.FromSeconds(2.5));
        Assert.False(overlay.IsVisible(TimeSpan.FromSeconds(10)));

        overlay.Moved(TimeSpan.FromSeconds(10));
        Assert.True(overlay.IsVisible(TimeSpan.FromSeconds(12)));
        Assert.False(overlay.IsVisible(TimeSpan.FromSeconds(12.6)));

        overlay.Hovered = true;
        Assert.True(overlay.IsVisible(TimeSpan.FromSeconds(60)));
        overlay.Hovered = false;
        overlay.Hide();
        Assert.False(overlay.IsVisible(TimeSpan.FromSeconds(10.1)));

        Assert.Equal(0.25, OverlayAutoHide.Fraction(5, 20));
        Assert.Equal(1, OverlayAutoHide.Fraction(30, 20));
        Assert.Equal(0, OverlayAutoHide.Fraction(5, 0));
    }

    [Fact]
    public void LaOtraPantalla_EsLaMayorDondeNoEstaElEditor()
    {
        var main = new ScreenRect(0, 0, 1920, 1080);
        var side = new ScreenRect(1920, 0, 1280, 720);
        var big = new ScreenRect(-3840, 0, 3840, 2160);
        var editor = new ScreenRect(100, 100, 1440, 900);

        Assert.Equal(-1, SecondScreen.Pick([main], editor));
        Assert.Equal(1, SecondScreen.Pick([main, side], editor));
        Assert.Equal(2, SecondScreen.Pick([main, side, big], editor));
        Assert.Equal(0, SecondScreen.Pick([main, side], new ScreenRect(2000, 50, 1000, 600)));
    }
}
