using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Rodaje.App.Services;
using Rodaje.App.Services.Interchange;
using Rodaje.App.Shell;
using Rodaje.Core.Interchange;
using Rodaje.Core.Model;

namespace Rodaje.App.Views.Interchange;

/// <summary>
/// El informe de pérdidas del intercambio: el resumen agrupado antes de exportar («12 clips pierden el color») y la lista
/// completa con el TC de la secuencia después, con «Copiar» y «Guardar como .txt».
/// </summary>
internal static class InterchangeReportDialog
{
    /// <summary>Antes de exportar: lo que no llega igual a <paramref name="format"/>. Verdadero si se sigue.</summary>
    public static async Task<bool> ConfirmAsync(EditorServices services, InterchangeFormat format, InterchangeReport preview)
    {
        if (services.Shell.XamlRoot is not { } root)
        {
            return true;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = $"Exportar a {Name(format)}",
            Content = Scrolling(new TextBlock
            {
                Text = "Esto no llega igual al otro programa:\n\n• " + string.Join("\n• ", preview.Summary()),
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
            }),
            PrimaryButtonText = "Exportar",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Primary,
        };
        return await DialogGate.ShowAsync(dialog) == ContentDialogResult.Primary;
    }

    /// <summary>La lista completa de un intercambio ya hecho.</summary>
    public static async Task ShowAsync(EditorServices services, string title, InterchangeReport report, Sequence sequence, InterchangeOptions options)
    {
        if (services.Shell.XamlRoot is not { } root)
        {
            return;
        }

        string text = InterchangeService.ReportText(report, sequence, options);
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = $"{title}: informe",
            Content = Scrolling(new TextBlock
            {
                Text = text.Length == 0 ? "Todo llegó igual." : text,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            }),
            PrimaryButtonText = "Copiar",
            SecondaryButtonText = "Guardar como .txt",
            CloseButtonText = "Cerrar",
            DefaultButton = ContentDialogButton.Close,
        };
        ContentDialogResult? result = await DialogGate.ShowAsync(dialog);
        if (result == ContentDialogResult.Primary)
        {
            var package = new global::Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(text);
            global::Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        }
        else if (result == ContentDialogResult.Secondary
            && FileDialogs.Save(InterchangeCommands.Owner(services), "Guardar el informe", $"{sequence.Name} - informe", "txt",
                services.Settings.Current.LastProjectFolder, ("Texto (*.txt)", "*.txt")) is { } path)
        {
            await File.WriteAllTextAsync(path, text, new UTF8Encoding(true));
        }
    }

    private static ScrollViewer Scrolling(UIElement content) => new() { Content = content, MaxHeight = 420, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

    private static string Name(InterchangeFormat format) => format switch
    {
        InterchangeFormat.Edl => "EDL CMX 3600",
        InterchangeFormat.Fcpxml => "FCPXML",
        _ => "OpenTimelineIO",
    };
}
