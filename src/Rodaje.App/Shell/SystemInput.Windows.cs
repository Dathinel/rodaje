namespace Rodaje.App.Shell;

internal static class SystemInput
{
    /// <summary>Tiempo máximo entre los dos clics de un doble clic, el que tiene puesto Windows.</summary>
    public static uint DoubleClickMilliseconds => TerraFX.Interop.Windows.Windows.GetDoubleClickTime();
}