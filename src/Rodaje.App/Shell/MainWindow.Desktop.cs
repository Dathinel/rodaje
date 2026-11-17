namespace Rodaje.App.Shell;

/// <summary>La ventana principal en Linux: los puntos por pulgada salen de la escala de XAML.</summary>
internal sealed partial class MainWindow
{
    private uint WindowDpi() => (uint)Math.Round(96 * (Root.XamlRoot?.RasterizationScale ?? 1));

    /// <summary>
    /// Autoprueba y medición. En Linux la ventana se muestra igual (en la CI corre en Xvfb, que nadie ve): X11 no tiene
    /// ventanas que no se activen como las de Win32.
    /// </summary>
    internal void ShowInBackground() => Activate();
}