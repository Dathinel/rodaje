using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Rodaje.App.Services.Collab;
using Rodaje.App.Shell;
using Rodaje.App.Views.Inspector;

namespace Rodaje.App.Views.Collab;

/// <summary>
/// La pregunta que ve quien hospeda cuando un invitado pide el original de un medio («Ana pide el original de
/// clip-a (1,2 GB)»): Enviar o No enviar, con la casilla «Enviar todos los que pida en esta sala» (vale solo para esa
/// persona hasta que termina la sala). Si hay otro diálogo abierto espera su turno; si nadie contesta, el que llama
/// (OriginalShareHost, diez minutos) lo resuelve como silencio y el invitado oye que no hubo respuesta.
/// </summary>
internal static class OriginalApproval
{
    public static async Task<OriginalDecision> AskAsync(XamlRoot? root, OriginalAsk ask, CancellationToken cancellationToken)
    {
        if (root is null)
        {
            return OriginalDecision.Refuse;
        }

        while (DialogGate.IsOpen)
        {
            await Task.Delay(400, cancellationToken);
        }

        var sendAll = new CheckBox
        {
            Content = "Enviar todos los que pida en esta sala",
            IsEnabled = true,
        };
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "Pedido de un original",
            Content = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new TextBlock { Text = ask.Text, TextWrapping = TextWrapping.Wrap },
                    InviteDialog.Hint("El archivo sale entero de tu equipo y viaja cifrado por la conexión de la sala."),
                    sendAll,
                },
            },
            PrimaryButtonText = "Enviar",
            CloseButtonText = "No enviar",
            DefaultButton = ContentDialogButton.Close,
        };
        // Si se agota el plazo o se cierra la sala, el diálogo se retira solo.
        DispatcherQueue dispatcher = DispatcherQueue.GetForCurrentThread();
        using CancellationTokenRegistration hide = cancellationToken.Register(() => dispatcher.RunLater(dialog.Hide));
        ContentDialogResult? result = await DialogGate.ShowAsync(dialog);
        cancellationToken.ThrowIfCancellationRequested();
        return result == ContentDialogResult.Primary
            ? sendAll.IsChecked == true ? OriginalDecision.SendAll : OriginalDecision.Send
            : OriginalDecision.Refuse;
    }
}
