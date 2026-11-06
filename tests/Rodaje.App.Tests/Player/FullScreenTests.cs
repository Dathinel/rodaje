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

    [Fact]
    public void LaVentanaQueNoCabe_SeRecortaAlAreaDeTrabajoYQuedaDentro()
    {
        // 1366×768 con la barra de tareas abajo (40 px), y un segundo monitor a la derecha.
        var work = new ScreenRect(0, 0, 1366, 728);
        var side = new ScreenRect(1366, 0, 1920, 1040);

        Assert.Equal(new ScreenRect(0, 0, 1366, 728), new ScreenRect(26, 26, 1440, 900).FitInside(work));
        Assert.Equal(new ScreenRect(100, 50, 800, 600), new ScreenRect(100, 50, 800, 600).FitInside(work));
        Assert.Equal(new ScreenRect(566, 128, 800, 600), new ScreenRect(1200, 700, 800, 600).FitInside(work));
        Assert.Equal(new ScreenRect(1366, 0, 1920, 1040), new ScreenRect(1300, -20, 2560, 1400).FitInside(side));
        Assert.Equal(new ScreenRect(26, 26, 1440, 900), new ScreenRect(26, 26, 1440, 900).FitInside(default));
    }
}
