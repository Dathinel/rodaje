using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Rodaje.App.Services;
using Rodaje.App.Services.Crashes;
using Rodaje.App.Shell;
using Rodaje.Core.Serialization;
using Rodaje.Export;

namespace Rodaje.App.Views.Export;

/// <summary>«Otros proyectos…»: varios archivos .rodaje entran a la cola con los ajustes de ahora, cada uno junto a su archivo.</summary>
internal sealed partial class ExportView
{
    private void OnProjectsClick(object sender, RoutedEventArgs e) => UiTask.Run(OnProjectsClickAsync());

    private async Task OnProjectsClickAsync()
    {
        if (_model is not { } model || XamlRoot is null)
        {
            return;
        }

        model.SetOutputPath(PathBox.Text);
        if (!model.CanExport)
        {
            return;
        }

        EditorSession session = _services.Session;
        string? projectFolder = session.FilePath is { } file ? Path.GetDirectoryName(file) : null;
        IReadOnlyList<string> picked = FileDialogs.Open(_hwnd, "Exportar otros proyectos", multiple: true, projectFolder, FileDialogs.ProjectFilter);
        if (picked.Count == 0)
        {
            return;
        }

        ProjectBatchPlan plan;
        try
        {
            plan = await Task.Run(() => ProjectBatch.Plan(picked, model.ResolvedSettings(), ProjectSerializer.Load,
                [.. _jobs.Entries.Where(entry => !entry.IsFinished).Select(entry => entry.OutputPath)], session.FilePath));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            session.Notify(NotificationSeverity.Warning, "No se pudieron preparar los proyectos", ex.Message);
            return;
        }

        string skipped = string.Join("\n", plan.Skipped.Select(s => $"• {Path.GetFileName(s.ProjectPath)}: {s.Problem}"));
        if (plan.Requests.Count == 0)
        {
            session.Notify(NotificationSeverity.Warning, "Ningún proyecto para exportar", skipped);
            return;
        }

        string presetName = model.Presets.FirstOrDefault(p => p.Id == model.PresetId)?.Name ?? "Personalizado";
        var text = new StackPanel { Spacing = 8 };
        text.Children.Add(new TextBlock
        {
            Text = $"{plan.Requests.Count} {(plan.Requests.Count == 1 ? "proyecto" : "proyectos")} con «{presetName}», la secuencia entera de cada uno, "
                + "junto a su archivo .rodaje (con un número si ya existe); rango y recorte, los de cada proyecto. Rendimiento y subtítulos, los de ahora.",
            TextWrapping = TextWrapping.Wrap,
        });
        text.Children.Add(new TextBlock { Text = string.Join("\n", plan.OutputNames.Select(n => "• " + n)), TextWrapping = TextWrapping.Wrap });
        if (plan.Skipped.Count > 0)
        {
            text.Children.Add(new TextBlock { Text = "Se saltan:\n" + skipped, TextWrapping = TextWrapping.Wrap, Opacity = 0.7 });
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "Exportar otros proyectos",
            Content = new ScrollViewer { Content = text, MaxHeight = 420 },
            PrimaryButtonText = "Exportar", CloseButtonText = "Cancelar", DefaultButton = ContentDialogButton.Primary,
        };
        if (await DialogGate.ShowAsync(dialog) != ContentDialogResult.Primary)
        {
            return;
        }

        IReadOnlyList<ExportEntry> entries = _jobs.EnqueueRange(plan.Requests);
        Started?.Invoke(this, entries[0]);
        session.Notify(NotificationSeverity.Informational, "Exportando otros proyectos",
            $"{entries.Count} en la cola; el avance y el orden están en Trabajos." + (plan.Skipped.Count > 0 ? $" Se saltaron {plan.Skipped.Count}." : ""));
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
