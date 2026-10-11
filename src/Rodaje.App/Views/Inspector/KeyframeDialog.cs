using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Rodaje.App.Shell;

namespace Rodaje.App.Views.Inspector;

/// <summary>Aviso antes de borrar keyframes (D-024): Cancelar es el botón por defecto y Esc también cancela.</summary>
internal static class KeyframeDialog
{
    public static string Count(int count) => count == 1 ? "1 keyframe" : $"{count} keyframes";

    public static async Task<bool> ConfirmAsync(XamlRoot? root, string message, string confirm)
    {
        if (DialogGate.IsOpen || root is null)
        {
            return false;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "¿Quitar los keyframes?",
            Content = new TextBlock { Text = message + " Se puede deshacer con Ctrl+Z.", TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = confirm,
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Close,
            RequestedTheme = ElementTheme.Dark,
        };
        return await DialogGate.ShowAsync(dialog) == ContentDialogResult.Primary;
    }

    /// <summary>«Restablecer posición X» con keyframes.</summary>
    public static Func<int, Task<bool>> ForReset(XamlRoot? root, string label) =>
        n => ConfirmAsync(root, $"Se borrarán {Count(n)} de «{label}» y quedará en su valor de fábrica.", "Quitar keyframes y restablecer");

    /// <summary>«Quitar keyframes» del menú del parámetro.</summary>
    public static Func<int, Task<bool>> ForClear(XamlRoot? root, string label) =>
        n => ConfirmAsync(root, $"Se borrarán {Count(n)} de «{label}». Quedará fijo en el valor que se ve en el cursor.", "Quitar keyframes");

    /// <summary>«Restablecer todo» de una sección.</summary>
    public static Func<int, Task<bool>> ForResetAll(XamlRoot? root, string what) =>
        n => ConfirmAsync(root, $"Se borrarán {Count(n)} de {what} y todo quedará en sus valores de fábrica.", "Quitar keyframes y restablecer");
}
