using Rodaje.App.Views.Player;

namespace Rodaje.App.Shell;

/// <summary>
/// La ventana principal en Linux: con la barra de título del sistema (Uno no extiende el contenido sobre la de X11 ni
/// tiene regiones de arrastre), sin área de trabajo del monitor y con los puntos por pulgada de la escala de XAML.
/// </summary>
internal sealed partial class MainWindow
{
    private uint WindowDpi() => (uint)Math.Round(96 * (Root.XamlRoot?.RasterizationScale ?? 1));

    /// <summary>
    /// Autoprueba y medición. En Linux la ventana se muestra igual (en la CI corre en Xvfb, que nadie ve): X11 no tiene
    /// ventanas que no se activen como las de Win32.
    /// </summary>
    internal void ShowInBackground() => Activate();

    private static ScreenRect FitInWorkArea(ScreenRect wanted) => wanted;

    // shortcut: Uno en X11 no implementa mover ni cambiar el tamaño de AppWindow; la ventana abre con el tamaño de Uno y no recuerda el guardado.
    private static void MoveAndResize(ScreenRect rect) => _ = rect;

    private static double CaptionButtonsWidth() => 0;

    private static void SetUpTitleBar()
    {
    }

    private static void UpdateDragRegion()
    {
    }
}