using Rodaje.Render;

namespace Rodaje.App.Views.Player;

/// <summary>Un rectángulo en unidades del panel (DIP).</summary>
public readonly record struct PanelRect(double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;

    public double Bottom => Top + Height;
}

/// <summary>
/// Dónde queda la imagen del proyecto en el visor, sin WinUI. El render y el XAML la sacan de aquí: el render pinta en
/// <see cref="Image"/> (píxeles físicos enteros del búfer) y el contorno, los márgenes, las asas y el texto del visor
/// vacío usan ese mismo rectángulo pasado a DIP, así coinciden al píxel con cualquier escala de pantalla.
/// </summary>
public static class ViewerGeometry
{
    /// <summary>Tamaño del búfer de la cadena para un panel de <paramref name="width"/> × <paramref name="height"/> DIP.</summary>
    public static (int Width, int Height) PanelPixels(double width, double height, double scaleX, double scaleY) =>
        (Math.Max(1, (int)Math.Round(width * scaleX)), Math.Max(1, (int)Math.Round(height * scaleY)));

    /// <summary>
    /// La imagen en el búfer: ajustada y centrada con <paramref name="zoom"/> 0, o a <paramref name="zoom"/> píxeles
    /// físicos por píxel de la secuencia. Siempre con la forma de la secuencia, no con la de la composición (que puede
    /// ir a la mitad o a un cuarto y redondear distinto).
    /// </summary>
    public static PixelRect Image(int panelWidth, int panelHeight, int sequenceWidth, int sequenceHeight, double zoom) =>
        zoom > 0
            ? VideoLayout.Centered(panelWidth, panelHeight, sequenceWidth, sequenceHeight, zoom)
            : VideoLayout.Fit(panelWidth, panelHeight, sequenceWidth, sequenceHeight);

    /// <summary>El rectángulo de píxeles físicos en DIP.</summary>
    public static PanelRect ToPanel(PixelRect pixels, double scaleX, double scaleY) =>
        new(pixels.X / scaleX, pixels.Y / scaleY, pixels.Width / scaleX, pixels.Height / scaleY);

    /// <summary>La esquina y la escala que usan las asas de los títulos (DIP por píxel de la secuencia).</summary>
    public static ImageFrame Frame(PixelRect pixels, int sequenceWidth, double scaleX, double scaleY) =>
        new(pixels.X / scaleX, pixels.Y / scaleY, pixels.Width / scaleX / Math.Max(1, sequenceWidth));

    /// <summary>
    /// El contorno del lienzo: un píxel físico justo fuera de la imagen por cada lado, en DIP. Su grosor es
    /// <c>1 / escala</c> DIP, así no cae entre dos píxeles ni a 125 % ni a 150 %.
    /// </summary>
    public static PanelRect Outline(PixelRect pixels, double scaleX, double scaleY) =>
        new((pixels.X - 1) / scaleX, (pixels.Y - 1) / scaleY, (pixels.Width + 2) / scaleX, (pixels.Height + 2) / scaleY);

    /// <summary>Todo de una vez para un panel en DIP; nulo sin tamaño.</summary>
    public static ViewerPlacement? Place(double panelWidth, double panelHeight, double scaleX, double scaleY,
        int sequenceWidth, int sequenceHeight, double zoom)
    {
        if (!(panelWidth > 0) || !(panelHeight > 0) || !(scaleX > 0) || !(scaleY > 0) || sequenceWidth <= 0 || sequenceHeight <= 0)
        {
            return null;
        }

        (int width, int height) = PanelPixels(panelWidth, panelHeight, scaleX, scaleY);
        PixelRect pixels = Image(width, height, sequenceWidth, sequenceHeight, zoom);
        return pixels.IsEmpty ? null : new ViewerPlacement(pixels, scaleX, scaleY, sequenceWidth, sequenceHeight);
    }
}

/// <summary>La imagen en el visor, en píxeles físicos y en DIP.</summary>
public readonly record struct ViewerPlacement(PixelRect Pixels, double ScaleX, double ScaleY, int SequenceWidth, int SequenceHeight)
{
    public PanelRect Image => ViewerGeometry.ToPanel(Pixels, ScaleX, ScaleY);

    public PanelRect Outline => ViewerGeometry.Outline(Pixels, ScaleX, ScaleY);

    public ImageFrame Frame => ViewerGeometry.Frame(Pixels, SequenceWidth, ScaleX, ScaleY);

    /// <summary>Un píxel físico en DIP: el grosor de las líneas del visor.</summary>
    public double Hairline => 1 / ScaleX;

    /// <summary>
    /// Un rectángulo dado en fracciones de la imagen (0 a 1) en DIP, con los bordes en píxeles físicos enteros: las guías
    /// y las zonas quedan nítidas y dentro de la imagen.
    /// </summary>
    public PanelRect Fraction(double left, double top, double width, double height)
    {
        double x0 = Math.Round(Pixels.X + (Math.Clamp(left, 0, 1) * Pixels.Width));
        double y0 = Math.Round(Pixels.Y + (Math.Clamp(top, 0, 1) * Pixels.Height));
        double x1 = Math.Round(Pixels.X + (Math.Clamp(left + width, 0, 1) * Pixels.Width));
        double y1 = Math.Round(Pixels.Y + (Math.Clamp(top + height, 0, 1) * Pixels.Height));
        return new PanelRect(x0 / ScaleX, y0 / ScaleY, Math.Max(0, x1 - x0) / ScaleX, Math.Max(0, y1 - y0) / ScaleY);
    }
}
