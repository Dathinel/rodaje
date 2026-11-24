using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Controls;
using Rodaje.App.Theme;

namespace Rodaje.App.Shell;

/// <summary>
/// Un solo <see cref="ContentDialog"/> a la vez en toda la ventana: WinUI lanza una excepción si se abre un segundo
/// (por ejemplo, Ctrl+N mientras pregunta si guardar), y en un <c>async void</c> eso cierra la app.
/// </summary>
internal static class DialogGate
{
    public static bool IsOpen { get; private set; }

    /// <summary>Muestra el diálogo; <c>null</c> si ya había otro abierto o WinUI no pudo mostrarlo.</summary>
    public static async Task<ContentDialogResult?> ShowAsync(ContentDialog dialog)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        if (IsOpen || dialog.XamlRoot is null)
        {
            return null;
        }

        // Un diálogo no hereda el tema de la ventana: toma el de WinUI que va con el tema de Rodaje.
        dialog.RequestedTheme = ThemeResources.RequestedTheme;
        IsOpen = true;
        try
        {
            return await dialog.ShowAsync();
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            return null;
        }
        finally
        {
            IsOpen = false;
        }
    }
}
