using System.Numerics;
using Rodaje.App.Services.Inspector;
using Rodaje.App.Views.Player;
using Rodaje.Core.Effects;
using Rodaje.Playback.Composition;
using Rodaje.Render;

namespace Rodaje.App.Tests.TimelineLogic;

/// <summary>Asas del recorte, ruedas de color y cuentagotas del croma, sin WinUI.</summary>
public class ImageToolsTests
{
    private static CropBox Box(CropRect crop, double? ratio = null) =>
        new("c", "r", LayerLayout.Place(1920, 1080, 1920, 1080, LayerTransform.Identity)!.Value, crop, ratio);

    [Fact]
    public void ElRecorteSeDibujaSobreLaImagenEnteraYSusAsasAciertan()
    {
        CropBox box = Box(new CropRect(0.25, 0, 0.25, 0.5));
        (Vector2[] image, Vector2[] crop, Vector2[] edges) = CropHandles.Outline(box);
        Assert.Equal(new Vector2(1920, 1080), image[2]);
        Assert.Equal(new Vector2(480, 0), crop[0]);
        Assert.Equal(new Vector2(1440, 540), crop[2]);
        Assert.Equal(new Vector2(960, 540), edges[3]);

        Assert.Equal(Core.Effects.CropGrip.BottomRight, CropHandles.Hit(crop, edges, new Vector2(1436, 543)));
        Assert.Equal(Core.Effects.CropGrip.Bottom, CropHandles.Hit(crop, edges, new Vector2(962, 538)));
        Assert.Equal(Core.Effects.CropGrip.Body, CropHandles.Hit(crop, edges, new Vector2(900, 300)));
        Assert.Equal(Core.Effects.CropGrip.None, CropHandles.Hit(crop, edges, new Vector2(100, 900)));
    }

    [Fact]
    public void ArrastrarUnaEsquinaConProporcionLaConserva()
    {
        CropBox box = Box(CropGeometry.FitAspect(CropRect.None, 1, 1920, 1080), 1);
        CropRect moved = CropHandles.Dragged(box, Core.Effects.CropGrip.BottomRight, new Vector2(1500, 1080), new Vector2(1300, 900));
        Assert.Equal(1, moved.Width * 1920 / (moved.Height * 1080), 3);
        Assert.True(moved.Width < box.Crop.Width);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(-0.3, 0.6)]
    [InlineData(0, -0.9)]
    public void LaRuedaVaYVuelveDelTinte(double x, double y)
    {
        (double r, double g, double b) = WheelMath.Tint(x, y, 0.2);
        Assert.Equal(0, r + g + b, 9);
        (double bx, double by) = WheelMath.Position(r, g, b, 0.2);
        Assert.Equal(x, bx, 9);
        Assert.Equal(y, by, 9);
        // Hacia 0° es rojo.
        Assert.True(WheelMath.Tint(1, 0, 0.2).R > 0.19);
    }

    [Fact]
    public void ElCuentagotasPromediaBajoElClic()
    {
        var rgba = new byte[10 * 10 * 4];
        for (int i = 0; i < 100; i++)
        {
            rgba[(i * 4) + 1] = 255;
            rgba[(i * 4) + 3] = 255;
        }

        (double R, double G, double B)? color = KeyColor.Average(rgba, 10, 10, 0.55, 0.45);
        Assert.Equal((0.0, 1.0, 0.0), color);
        Assert.Null(KeyColor.Average(rgba, 10, 10, 1.2, 0.5));
    }
}
