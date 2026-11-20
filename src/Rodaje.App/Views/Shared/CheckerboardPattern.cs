using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Rodaje.App.Views.Shared;

/// <summary>
/// Fondo de cuadros tipo ajedrez para previsualizar imágenes con transparencia sobre fondo neutro (D-050).
/// </summary>
internal static class CheckerboardPattern
{
    private static WriteableBitmap? _instance;

    public static WriteableBitmap Instance => _instance ??= Create(128, 72, 8);

    public static WriteableBitmap Create(int width, int height, int squareSize)
    {
        var bitmap = new WriteableBitmap(width, height);
        byte[] pixels = new byte[width * height * 4];
        const uint color1 = 0xFF2A2A2Au;
        const uint color2 = 0xFF1C1C1Cu;

        unsafe
        {
            fixed (byte* ptr = pixels)
            {
                uint* dst = (uint*)ptr;
                for (int y = 0; y < height; y++)
                {
                    int rowSquare = y / squareSize;
                    for (int x = 0; x < width; x++)
                    {
                        int colSquare = x / squareSize;
                        bool check = ((rowSquare + colSquare) & 1) == 0;
                        dst[(y * width) + x] = check ? color1 : color2;
                    }
                }
            }
        }

        pixels.CopyTo(bitmap.PixelBuffer);
        bitmap.Invalidate();
        return bitmap;
    }
}
