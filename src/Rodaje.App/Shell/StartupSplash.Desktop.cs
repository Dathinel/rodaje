using Microsoft.UI.Windowing;
using Rodaje.App.Services.Settings;

namespace Rodaje.App.Shell;

/// <summary>En Linux no hay armazón de Win32: la ventana de Uno es lo primero que se ve.</summary>
internal static class StartupSplash
{
    /// <summary>Coordenada fuera de cualquier monitor para las ventanas de una corrida de fondo.</summary>
    public const int OffScreen = -20000;

    public static bool IsShown => false;

    public static void Show(AppSettings settings, bool background)
    {
    }

    public static void PlaceOver(AppWindow window, nint hwnd)
    {
    }

    public static void Reveal(nint hwnd)
    {
    }

    public static void Close()
    {
    }
}