using System.Numerics;
using Rodaje.App.Views.Player;
using Rodaje.Render;

namespace Rodaje.App.Tests.Player;

/// <summary>
/// El visor: la imagen del render, el contorno, las guías y las asas salen del mismo rectángulo en píxeles físicos, en
/// todos los formatos, tamaños de panel y escalas de pantalla.
/// </summary>
public class ViewerGeometryTests
{
    public static TheoryData<int, int, double, double, double> Casos()
    {
        var data = new TheoryData<int, int, double, double, double>();
        (int, int)[] formats = [(1920, 1080), (1080, 1920), (1080, 1080), (1080, 1350)];
        (double, double)[] panels = [(640, 360), (667.2, 401.6), (300, 700), (1013, 517), (181, 97)];
        double[] scales = [1.0, 1.25, 1.5];
        foreach ((int w, int h) in formats)
        {
            foreach ((double pw, double ph) in panels)
            {
                foreach (double s in scales)
                {
                    // XAML redondea el diseño a píxeles físicos: el tamaño del panel en DIP es múltiplo de 1/escala.
                    data.Add(w, h, Math.Round(pw * s) / s, Math.Round(ph * s) / s, s);
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Casos))]
    public void LaImagenQuedaCentradaYConLaFormaDelProyecto(int width, int height, double panelWidth, double panelHeight, double scale)
    {
        ViewerPlacement p = ViewerGeometry.Place(panelWidth, panelHeight, scale, scale, width, height, 0)!.Value;
        (int bufferWidth, int bufferHeight) = ViewerGeometry.PanelPixels(panelWidth, panelHeight, scale, scale);
        PixelRect r = p.Pixels;

        Assert.True(r.X >= 0 && r.Y >= 0 && r.X + r.Width <= bufferWidth && r.Y + r.Height <= bufferHeight);
        Assert.True(r.Width == bufferWidth || r.Height == bufferHeight, "ajustada: llena el ancho o el alto");
        // Centrada al píxel: lo que sobra a cada lado difiere en un píxel como mucho.
        Assert.InRange(bufferWidth - r.Width - (2 * r.X), 0, 1);
        Assert.InRange(bufferHeight - r.Height - (2 * r.Y), 0, 1);
        // La forma del proyecto, con medio píxel de redondeo.
        Assert.InRange(Math.Abs((r.Width * (double)height / width) - r.Height), 0, 1);
    }

    [Theory]
    [MemberData(nameof(Casos))]
    public void ContornoGuiasYAsasCompartenElRectanguloDelRender(int width, int height, double panelWidth, double panelHeight, double scale)
    {
        ViewerPlacement p = ViewerGeometry.Place(panelWidth, panelHeight, scale, scale, width, height, 0)!.Value;
        PixelRect r = p.Pixels;

        // El contorno: un píxel físico justo fuera de la imagen, en bordes enteros y del grosor de un píxel.
        PanelRect outline = p.Outline;
        AssertPixel(outline.Left * scale, r.X - 1);
        AssertPixel(outline.Top * scale, r.Y - 1);
        AssertPixel(outline.Right * scale, r.X + r.Width + 1);
        AssertPixel(outline.Bottom * scale, r.Y + r.Height + 1);
        AssertPixel(p.Hairline * scale, 1);

        // La imagen en DIP, de vuelta a píxeles, es la del render.
        PanelRect image = p.Image;
        AssertPixel(image.Left * scale, r.X);
        AssertPixel(image.Right * scale, r.X + r.Width);
        AssertPixel(image.Bottom * scale, r.Y + r.Height);

        // Las asas: las esquinas de la secuencia caen en las de la imagen.
        ImageFrame frame = p.Frame;
        Vector2 topLeft = frame.ToPanel(Vector2.Zero);
        Vector2 bottomRight = frame.ToPanel(new Vector2(width, height));
        Assert.Equal(image.Left, topLeft.X, 3);
        Assert.Equal(image.Top, topLeft.Y, 3);
        Assert.Equal(image.Right, bottomRight.X, 3);
        Assert.InRange(Math.Abs(bottomRight.Y - image.Bottom) * scale, 0, 1);

        // Las guías: dentro de la imagen y en píxeles enteros.
        PanelRect title = p.Fraction(0.05, 0.05, 0.9, 0.9);
        Assert.True(title.Left >= image.Left && title.Right <= image.Right + 1e-9 && title.Top >= image.Top && title.Bottom <= image.Bottom + 1e-9);
        AssertPixel(title.Left * scale, Math.Round(title.Left * scale));
        AssertPixel(title.Bottom * scale, Math.Round(title.Bottom * scale));
    }

    [Fact]
    public void ConZoomUnPixelDeLaSecuenciaEsUnPixelFisico()
    {
        ViewerPlacement p = ViewerGeometry.Place(800, 450, 1.5, 1.5, 1080, 1920, 1)!.Value;
        Assert.Equal(1080, p.Pixels.Width);
        Assert.Equal(1920, p.Pixels.Height);
        Assert.Equal((1200 - 1080) / 2, p.Pixels.X);
        Assert.Equal(1 / 1.5, p.Frame.Scale, 9);
    }

    [Fact]
    public void ElRenderNoUsaLaFormaDeLaComposicionReducida()
    {
        // A un cuarto, 1080×1350 compone a 270×337: ajustar con esa forma daría otro rectángulo que el del contorno.
        PixelRect sequence = ViewerGeometry.Image(1001, 701, 1080, 1350, 0);
        PixelRect quarter = VideoLayout.Fit(1001, 701, 270, 337);
        Assert.NotEqual(sequence, quarter);
        Assert.Equal(sequence, ViewerGeometry.Place(1001, 701, 1, 1, 1080, 1350, 0)!.Value.Pixels);
    }

    [Fact]
    public void SinTamanoNoHayLienzo()
    {
        Assert.Null(ViewerGeometry.Place(0, 300, 1, 1, 1920, 1080, 0));
        Assert.Null(ViewerGeometry.Place(300, 300, 1, 1, 0, 1080, 0));
    }

    private static void AssertPixel(double actual, double expected) => Assert.Equal(expected, actual, 6);
}
