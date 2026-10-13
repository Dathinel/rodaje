using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Rodaje.App.Services;
using Rodaje.App.Services.Crashes;
using Rodaje.App.Shell;
using Rodaje.Export;

namespace Rodaje.App.Views.Export;

/// <summary>«Varias versiones…»: se marcan varios preajustes y entran todos a la cola con los mismos rango y carpeta.</summary>
internal sealed partial class ExportView
{
    private void OnVersionsClick(object sender, RoutedEventArgs e) => UiTask.Run(OnVersionsClickAsync(sender, e));

    private async Task OnVersionsClickAsync(object sender, RoutedEventArgs e)
    {
        if (_model is not { } model || XamlRoot is null)
        {
            return;
        }

        model.SetOutputPath(PathBox.Text);
        var boxes = new List<CheckBox>();
        var list = new StackPanel { Spacing = 2 };
        list.Children.Add(new TextBlock
        {
            Text = $"Se guardan en «{Path.GetDirectoryName(model.OutputPath)}» con el nombre del preajuste al final. Rango y rendimiento, los de ahora.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        });
        foreach (ExportPreset preset in model.Presets)
        {
            var box = new CheckBox { Content = preset.Name, Tag = preset.Id, IsChecked = preset.Id == model.PresetId };
            ToolTipService.SetToolTip(box, preset.Description);
            boxes.Add(box);
            list.Children.Add(box);
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "Exportar varias versiones",
            Content = new ScrollViewer { Content = list, MaxHeight = 420 },
            PrimaryButtonText = "Exportar", CloseButtonText = "Cancelar", DefaultButton = ContentDialogButton.Primary,
            RequestedTheme = ElementTheme.Dark,
        };
        if (await DialogGate.ShowAsync(dialog) != ContentDialogResult.Primary)
        {
            return;
        }

        string[] ids = [.. boxes.Where(b => b.IsChecked == true).Select(b => (string)b.Tag)];
        if (ids.Length == 0)
        {
            return;
        }

        EditorSession session = _services.Session;
        string? projectFolder = session.FilePath is { } file ? Path.GetDirectoryName(file) : null;
        IReadOnlyList<ExportRequest> requests;
        try
        {
            requests = model.CreateVersionRequests(ids, session.Project.Clone(), session.SequenceId, projectFolder);
        }
        catch (ArgumentException ex)
        {
            session.Notify(NotificationSeverity.Warning, "No se pudieron preparar las versiones", ex.Message);
            return;
        }

        IReadOnlyList<ExportEntry> entries = _jobs.EnqueueRange(requests);
        Started?.Invoke(this, entries[0]);
        session.Notify(NotificationSeverity.Informational, "Exportando varias versiones",
            $"{entries.Count} versiones en la cola; el avance y el orden están en Trabajos.");
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
