using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Rodaje.App.Services.Settings;
using Windows.Graphics;

namespace Rodaje.App.Shell;

/// <summary>
/// Armazón de la ventana en Win32 y GDI, antes de cargar WinUI: a los ~30 ms de abrir el programa ya hay una ventana
/// oscura con los paneles en su sitio (biblioteca, visor, panel derecho y línea de tiempo con los anchos guardados).
/// WinUI tarda unos 0,4 s en caliente y 0,7 s en frío en tener su primer cuadro; su ventana se pone en el mismo lugar,
/// oculta para DWM (cloak) hasta ese cuadro, y entonces se muestra y el armazón se cierra. Sin parpadeo blanco.
/// </summary>
internal static unsafe partial class StartupSplash
{
    private const string ClassName = "Rodaje.Armazon";
    private const uint BackgroundColor = 0x101010; // COLORREF (BGR) de BackgroundColor de Palette.xaml
    private const uint PanelColor = 0x1D1D1D;
    private const uint AccentColor = 0x3A83E8; // #E8833A
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmCloak = 13;
    private const int DwmCaptionColor = 35;
    private const int DwmTextColor = 36;

    private static nint _hwnd;
    private static nint _panelBrush;
    private static double _library;
    private static double _inspector;
    private static double _timeline;

    public static bool IsShown => _hwnd != 0;

    /// <summary>Crea y pinta el armazón. Si algo falla, no hay armazón y la app sigue como siempre.</summary>
    public static void Show(AppSettings settings)
    {
        try
        {
            _library = settings.LibraryWidth;
            _inspector = settings.InspectorWidth;
            _timeline = settings.TimelineHeight;
            nint instance = GetModuleHandleW(null);
            _panelBrush = CreateSolidBrush(PanelColor);
            string iconPath = Path.Combine(AppContext.BaseDirectory, AppIcon.FileName);
            fixed (char* name = ClassName)
            {
                var wc = new WNDCLASSEXW
                {
                    cbSize = (uint)sizeof(WNDCLASSEXW),
                    lpfnWndProc = &WindowProc,
                    hInstance = instance,
                    hIcon = LoadIconFile(iconPath, GetSystemMetrics(11)), // SM_CXICON
                    hIconSm = LoadIconFile(iconPath, GetSystemMetrics(49)), // SM_CXSMICON
                    hCursor = LoadCursorW(0, 32650), // IDC_APPSTARTING
                    hbrBackground = CreateSolidBrush(BackgroundColor),
                    lpszClassName = name,
                };
                if (RegisterClassExW(&wc) == 0)
                {
                    return;
                }
            }

            double scale = GetDpiForSystem() / 96.0;
            int width = (int)((settings.WindowWidth > 0 ? settings.WindowWidth : 1440) * scale);
            int height = (int)((settings.WindowHeight > 0 ? settings.WindowHeight : 900) * scale);
            const uint OverlappedWindow = 0x00CF0000;
            const int UseDefault = unchecked((int)0x80000000);
            _hwnd = CreateWindowExW(0, ClassName, "Rodaje", OverlappedWindow, UseDefault, UseDefault, width, height, 0, 0, instance, 0);
            if (_hwnd == 0)
            {
                return;
            }

            int on = 1;
            _ = DwmSetWindowAttribute(_hwnd, DwmUseImmersiveDarkMode, &on, sizeof(int));
            uint caption = BackgroundColor;
            uint text = AccentColor;
            _ = DwmSetWindowAttribute(_hwnd, DwmCaptionColor, &caption, sizeof(uint));
            _ = DwmSetWindowAttribute(_hwnd, DwmTextColor, &text, sizeof(uint));
            // SW_SHOWDEFAULT respeta el modo con que se abrió el programa (un acceso directo puede pedirlo maximizado).
            _ = ShowWindow(_hwnd, settings.WindowMaximized ? 3 : 10);
            _ = UpdateWindow(_hwnd);
            Program.Metrics.Milestone("armazon_visible");
        }
#pragma warning disable CA1031 // El armazón es un adorno: si falla, se arranca sin él.
        catch (Exception)
#pragma warning restore CA1031
        {
            Close();
        }
    }

    /// <summary>Pone la ventana de WinUI donde está el armazón y la oculta para DWM hasta su primer cuadro.</summary>
    public static void PlaceOver(AppWindow window, nint hwnd)
    {
        if (_hwnd == 0)
        {
            return;
        }

        if (IsZoomed(_hwnd) == 0 && GetWindowRect(_hwnd, out RECT r) != 0)
        {
            window.MoveAndResize(new RectInt32(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top));
        }

        int on = 1;
        _ = DwmSetWindowAttribute(hwnd, DwmCloak, &on, sizeof(int));
    }

    /// <summary>Muestra la ventana de WinUI (ya pintada) y cierra el armazón.</summary>
    public static void Reveal(nint hwnd)
    {
        int off = 0;
        _ = DwmSetWindowAttribute(hwnd, DwmCloak, &off, sizeof(int));
        if (_hwnd != 0)
        {
            Close();
            Program.Metrics.Milestone("armazon_cerrado");
        }
    }

    public static void Close()
    {
        if (_hwnd != 0)
        {
            _ = DestroyWindow(_hwnd);
            _hwnd = 0;
        }
    }

    [UnmanagedCallersOnly]
    private static nint WindowProc(nint hwnd, uint message, nint wParam, nint lParam)
    {
        const uint Paint = 0x000F;
        const uint CloseMessage = 0x0010;
        if (message == Paint)
        {
            PaintPanels(hwnd);
            return 0;
        }

        // Cerrar el armazón no cierra el programa a medio cargar: la ventana de verdad llega enseguida.
        return message == CloseMessage ? 0 : DefWindowProcW(hwnd, message, wParam, lParam);
    }

    private static void PaintPanels(nint hwnd)
    {
        PAINTSTRUCT ps;
        nint dc = BeginPaint(hwnd, &ps);
        if (GetClientRect(hwnd, out RECT client) != 0)
        {
            double s = GetDpiForWindow(hwnd) / 96.0;
            int gap = (int)(4 * s);
            int top = (int)(8 * s);
            int w = client.Right;
            int h = client.Bottom;
            int library = (int)(_library * s);
            int inspector = (int)(_inspector * s);
            int timelineTop = Math.Max(top + (int)(240 * s), h - (int)(_timeline * s));
            Fill(dc, 0, top, library, timelineTop - gap);
            Fill(dc, library + gap, top, w - inspector - gap, timelineTop - gap);
            Fill(dc, w - inspector, top, w, timelineTop - gap);
            Fill(dc, 0, timelineTop, w, h);
        }

        _ = EndPaint(hwnd, &ps);
    }

    private static void Fill(nint dc, int left, int top, int right, int bottom)
    {
        if (right <= left || bottom <= top)
        {
            return;
        }

        var r = new RECT { Left = left, Top = top, Right = right, Bottom = bottom };
        _ = FillRect(dc, &r, _panelBrush);
    }

    private static nint LoadIconFile(string path, int size)
    {
        const uint ImageIcon = 1;
        const uint LoadFromFile = 0x10;
        return LoadImageW(0, path, ImageIcon, size, size, LoadFromFile);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public delegate* unmanaged<nint, uint, nint, nint, nint> lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public char* lpszMenuName;
        public char* lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PAINTSTRUCT
    {
        public nint hdc;
        public int fErase;
        public RECT rcPaint;
        public int fRestore;
        public int fIncUpdate;
        public fixed byte rgbReserved[32];
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandleW(string? name);

    [LibraryImport("user32.dll")]
    private static partial ushort RegisterClassExW(WNDCLASSEXW* wc);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateWindowExW(uint exStyle, string className, string title, uint style, int x, int y, int width, int height,
        nint parent, nint menu, nint instance, nint param);

    [LibraryImport("user32.dll")]
    private static partial nint DefWindowProcW(nint hwnd, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    private static partial int ShowWindow(nint hwnd, int command);

    [LibraryImport("user32.dll")]
    private static partial int UpdateWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    private static partial int DestroyWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    private static partial int IsZoomed(nint hwnd);

    [LibraryImport("user32.dll")]
    private static partial int GetWindowRect(nint hwnd, out RECT rect);

    [LibraryImport("user32.dll")]
    private static partial int GetClientRect(nint hwnd, out RECT rect);

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForSystem();

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    private static partial nint LoadCursorW(nint instance, nint name);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint LoadImageW(nint instance, string name, uint type, int width, int height, uint flags);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);

    [LibraryImport("user32.dll")]
    private static partial nint BeginPaint(nint hwnd, PAINTSTRUCT* paint);

    [LibraryImport("user32.dll")]
    private static partial int EndPaint(nint hwnd, PAINTSTRUCT* paint);

    [LibraryImport("user32.dll")]
    private static partial int FillRect(nint dc, RECT* rect, nint brush);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateSolidBrush(uint color);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(nint hwnd, int attribute, void* value, int size);
}
