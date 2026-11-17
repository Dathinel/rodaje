using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Rodaje.App.Views.Player;
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

    private ScreenRect FitInWorkArea(ScreenRect wanted)
    {
        RectInt32 work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        return wanted.FitInside(new ScreenRect(work.X, work.Y, work.Width, work.Height));
    }

    private void MoveAndResize(ScreenRect rect) => AppWindow.MoveAndResize(new RectInt32(rect.X, rect.Y, rect.Width, rect.Height));

    private double CaptionButtonsWidth() => AppWindow.TitleBar.RightInset;

    /// <summary>
    /// La barra de título propia. La región de arrastre no se deja a <c>SetTitleBar</c>: con la escala de la interfaz
    /// (Preferencias) la raíz se dibuja transformada y la región se calcula aquí en píxeles con esa escala y la del monitor
    /// (<see cref="TitleBarDragRegion"/>), y se recalcula al cambiar el tamaño de la ventana, de la barra o la escala.
    /// </summary>
    private void SetUpTitleBar()
    {
        AppWindowTitleBar bar = AppWindow.TitleBar;
        bar.ExtendsContentIntoTitleBar = true;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, AppIcon.FileName));
        bar.PreferredHeightOption = TitleBarHeightOption.Tall;
        bar.ButtonBackgroundColor = Colors.Transparent;
        bar.ButtonInactiveBackgroundColor = Colors.Transparent;
        ApplyTitleBarColors();
        Theme.ThemeResources.ThemeChanged += (_, _) => ApplyTitleBarColors();
        TitleBar.DragRegion.Loaded += (_, _) => UpdateDragRegion();
        TitleBar.DragRegion.SizeChanged += (_, _) => UpdateDragRegion();
    }

    // Los botones de la ventana (minimizar, maximizar, cerrar) siguen al tema, también cuando cambia en caliente.
    private void ApplyTitleBarColors()
    {
        AppWindowTitleBar bar = AppWindow.TitleBar;
        bar.ButtonForegroundColor = Theme.ThemeResources.SolidColor("TextBrush");
        bar.ButtonHoverForegroundColor = Theme.ThemeResources.SolidColor("TextBrush");
        bar.ButtonHoverBackgroundColor = Theme.ThemeResources.SolidColor("ControlHoverBrush");
        bar.ButtonPressedBackgroundColor = Theme.ThemeResources.SolidColor("ControlPressedBrush");
        bar.ButtonInactiveForegroundColor = Theme.ThemeResources.SolidColor("TextMutedBrush");
    }

    private void UpdateDragRegion()
    {
        // Antes de cargarse en el árbol (la activación llega antes), TransformToVisual lanza E_UNEXPECTED; Loaded lo repite.
        if (_closed || !TitleBar.DragRegion.IsLoaded)
        {
            return;
        }

        uint dpi = WindowDpi();
        global::Windows.Foundation.Point origin = TitleBar.DragRegion.TransformToVisual(Root).TransformPoint(new global::Windows.Foundation.Point(0, 0));
        AppWindowTitleBar bar = AppWindow.TitleBar;
        TitleBarDragRegion.Rect? region = TitleBarDragRegion.Compute(origin.X, origin.Y, TitleBar.DragRegion.ActualWidth, TitleBar.DragRegion.ActualHeight,
            _settings.Current.UiScale / 100.0, Math.Max(1u, dpi) / 96.0, AppWindow.ClientSize.Width, bar.LeftInset, bar.RightInset);
        RectInt32[] rects = region is { } r ? [new RectInt32(r.X, r.Y, r.Width, r.Height)] : [];
        bar.SetDragRectangles(rects);
    }
}
