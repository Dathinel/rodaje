using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;

namespace Rodaje.App.Shell;

/// <summary>
/// La ventana tal como la compone Windows, con la imagen de la cadena de intercambio incluida (<c>RenderTargetBitmap</c>
/// solo ve el XAML). Para la autoprueba: comprobar que el contorno del lienzo cae sobre el borde de la imagen.
/// </summary>
internal static unsafe class WindowCapture
{
    private const uint PwClientOnly = 0x1;
    private const uint PwRenderFullContent = 0x2;

    /// <summary>El área cliente en BGRA de arriba abajo; nulo si Windows no la dio.</summary>
    public static (byte[] Bgra, int Width, int Height)? Client(nint hwnd)
    {
        var window = (HWND)hwnd;
        RECT client;
        if (!GetClientRect(window, &client) || client.right <= 0 || client.bottom <= 0)
        {
            return null;
        }

        int width = client.right;
        int height = client.bottom;
        HDC screen = GetDC(HWND.NULL);
        HDC memory = CreateCompatibleDC(screen);
        var info = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = (uint)sizeof(BITMAPINFOHEADER),
                biWidth = width,
                biHeight = -height,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = BI.BI_RGB,
            },
        };
        void* bits;
        HBITMAP bitmap = CreateDIBSection(screen, &info, DIB_RGB_COLORS, &bits, HANDLE.NULL, 0);
        try
        {
            if (bitmap == HBITMAP.NULL)
            {
                return null;
            }

            HGDIOBJ previous = SelectObject(memory, (HGDIOBJ)bitmap);
            bool ok = PrintWindow(window, memory, PwClientOnly | PwRenderFullContent);
            SelectObject(memory, previous);
            if (!ok)
            {
                return null;
            }

            byte[] pixels = new ReadOnlySpan<byte>(bits, width * height * 4).ToArray();
            for (int i = 3; i < pixels.Length; i += 4)
            {
                pixels[i] = 0xFF;
            }

            return (pixels, width, height);
        }
        finally
        {
            if (bitmap != HBITMAP.NULL)
            {
                DeleteObject((HGDIOBJ)bitmap);
            }

            DeleteDC(memory);
            _ = ReleaseDC(HWND.NULL, screen);
        }
    }
}
