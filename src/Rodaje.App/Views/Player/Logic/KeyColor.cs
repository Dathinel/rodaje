namespace Rodaje.App.Views.Player;

/// <summary>El color clave a partir de un cuadro bajado a la CPU, sin WinUI.</summary>
public static class KeyColor
{
    /// <summary>Promedio de 5×5 píxeles alrededor del punto (fracciones del cuadro) en R'G'B' de 0 a 1; nulo fuera del cuadro.</summary>
    public static (double R, double G, double B)? Average(byte[] rgba, int width, int height, double u, double v)
    {
        ArgumentNullException.ThrowIfNull(rgba);
        if (!(u >= 0 && u < 1 && v >= 0 && v < 1) || width <= 0 || height <= 0 || rgba.Length < width * height * 4)
        {
            return null;
        }

        int cx = (int)(u * width), cy = (int)(v * height);
        double r = 0, g = 0, b = 0;
        int count = 0;
        for (int y = Math.Max(0, cy - 2); y <= Math.Min(height - 1, cy + 2); y++)
        {
            for (int x = Math.Max(0, cx - 2); x <= Math.Min(width - 1, cx + 2); x++)
            {
                int i = ((y * width) + x) * 4;
                r += rgba[i];
                g += rgba[i + 1];
                b += rgba[i + 2];
                count++;
            }
        }

        return (r / count / 255, g / count / 255, b / count / 255);
    }
}
