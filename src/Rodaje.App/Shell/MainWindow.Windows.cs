using Windows.Graphics;
using static TerraFX.Interop.Windows.Windows;

namespace Rodaje.App.Shell;

/// <summary>Lo de la ventana principal que llama a Win32.</summary>
internal sealed partial class MainWindow
{
    private uint WindowDpi() => GetDpiForWindow((TerraFX.Interop.Windows.HWND)_hwnd);

    /// <summary>
    /// Autoprueba y medición: la ventana se pinta fuera de pantalla, sin botón en la barra de tareas y sin poder activarse
    /// (deshabilitada para Win32: el foco de XAML no arrastra el del teclado de quien usa el equipo).
    /// </summary>
    internal void ShowInBackground()
    {
        var hwnd = (TerraFX.Interop.Windows.HWND)_hwnd;
        _ = SetWindowLongPtrW(hwnd, TerraFX.Interop.Windows.GWL.GWL_EXSTYLE,
            GetWindowLongPtrW(hwnd, TerraFX.Interop.Windows.GWL.GWL_EXSTYLE) | TerraFX.Interop.Windows.WS.WS_EX_NOACTIVATE | TerraFX.Interop.Windows.WS.WS_EX_TOOLWINDOW);
        _ = EnableWindow(hwnd, TerraFX.Interop.Windows.BOOL.FALSE);
        AppWindow.Move(new PointInt32(StartupSplash.OffScreen, StartupSplash.OffScreen));
        AppWindow.Show(false);
        // Sin activarse no llega Activated: lo que espera a la primera activación se hace aquí.
        OnFirstActivation();
    }
}
