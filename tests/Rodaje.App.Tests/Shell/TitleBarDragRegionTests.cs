using Rodaje.App.Shell;

namespace Rodaje.App.Tests.Shell;

/// <summary>La región de arrastre de la barra de título con la escala de la interfaz (90–125 %) y la del monitor.</summary>
public sealed class TitleBarDragRegionTests
{
    [Theory]
    [InlineData(1.0, 1.0, 200, 0, 600, 40)]
    [InlineData(0.9, 1.0, 180, 0, 540, 36)]
    [InlineData(1.1, 1.0, 220, 0, 660, 44)]
    [InlineData(1.25, 1.0, 250, 0, 750, 50)]
    [InlineData(1.25, 1.5, 375, 0, 1125, 75)]
    public void LaRegionSeEscalaConLaInterfazYElMonitor(double uiScale, double dpiScale, int x, int y, int width, int height)
    {
        // La barra ocupa de 200 a 800 DIP sin escalar en una ventana ancha, sin botones que la recorten.
        TitleBarDragRegion.Rect? region = TitleBarDragRegion.Compute(200, 0, 600, 40, uiScale, dpiScale, clientWidth: 4000, leftInset: 0, rightInset: 0);
        Assert.Equal(new TitleBarDragRegion.Rect(x, y, width, height), region);
    }

    [Fact]
    public void LosBotonesDeLaVentanaRecortanLaRegion()
    {
        // A 125 % la región llegaría al píxel 1000 pero los botones (138 px) empiezan en 862 en una ventana de 1000.
        TitleBarDragRegion.Rect region = TitleBarDragRegion.Compute(200, 0, 600, 40, 1.25, 1, clientWidth: 1000, leftInset: 0, rightInset: 138)!.Value;
        Assert.Equal(250, region.X);
        Assert.Equal(862, region.X + region.Width);
        // Un margen izquierdo (botones a la izquierda) también recorta; y a 90 % nada se sale por la izquierda.
        Assert.Equal(50, TitleBarDragRegion.Compute(0, 0, 600, 40, 0.9, 1, 1000, leftInset: 50, rightInset: 0)!.Value.X);
    }

    [Fact]
    public void SinSitioNoHayRegion()
    {
        Assert.Null(TitleBarDragRegion.Compute(200, 0, 0, 40, 1, 1, 1000, 0, 0));
        Assert.Null(TitleBarDragRegion.Compute(200, 0, 600, 40, 1, 1, 0, 0, 0));
        Assert.Null(TitleBarDragRegion.Compute(900, 0, 600, 40, 1, 1, 1000, 0, 138));
        Assert.Null(TitleBarDragRegion.Compute(200, 0, 600, 40, double.NaN, 1, 1000, 0, 0));
        Assert.Null(TitleBarDragRegion.Compute(200, 0, 600, 40, 1, 0, 1000, 0, 0));
    }
}
