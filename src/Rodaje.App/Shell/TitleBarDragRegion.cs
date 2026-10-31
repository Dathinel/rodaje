namespace Rodaje.App.Shell;

/// <summary>
/// La región de arrastre de la barra de título en píxeles físicos, como la pide <c>AppWindow.TitleBar.SetDragRectangles</c>:
/// la posición y el tamaño del elemento en la raíz (DIP sin escalar) por la escala de la interfaz (Preferencias) y por la
/// del monitor, recortada a los botones de la ventana (los márgenes de la barra). Sin WinUI, para poder probarla a
/// 90, 110 y 125 %.
/// </summary>
internal static class TitleBarDragRegion
{
    public readonly record struct Rect(int X, int Y, int Width, int Height);

    /// <summary>
    /// <paramref name="left"/>, <paramref name="top"/>, <paramref name="width"/> y <paramref name="height"/> van en DIP de la
    /// raíz sin escalar; <paramref name="clientWidth"/> y los márgenes, en píxeles. <c>null</c> si no queda nada que arrastrar.
    /// </summary>
    public static Rect? Compute(double left, double top, double width, double height, double uiScale, double dpiScale, int clientWidth, int leftInset, int rightInset)
    {
        if (!(width > 0) || !(height > 0) || !(uiScale > 0) || !(dpiScale > 0) || !double.IsFinite(uiScale * dpiScale) || clientWidth <= 0)
        {
            return null;
        }

        double k = uiScale * dpiScale;
        int x = Math.Max((int)Math.Round(left * k), Math.Max(0, leftInset));
        int y = Math.Max(0, (int)Math.Round(top * k));
        int right = Math.Min((int)Math.Round((left + width) * k), clientWidth - Math.Max(0, rightInset));
        int bottom = (int)Math.Round((top + height) * k);
        return right > x && bottom > y ? new Rect(x, y, right - x, bottom - y) : null;
    }
}
