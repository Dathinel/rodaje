using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Rodaje.App.Services;
using Rodaje.App.Services.Format;
using Rodaje.App.Services.LongActions;
using Rodaje.App.Services.Modules;
using Rodaje.App.Services.Settings;
using Rodaje.App.Shell;
using Rodaje.App.Views.Analysis;
using Rodaje.App.Views.Shared;
using Rodaje.Core.Model;
using Rodaje.Media.Proxies;
using Rodaje.Modules.Protocol;

namespace Rodaje.App.Views.Raw;

/// <summary>
/// «Importar CinemaDNG…» (Archivo): se elige un cuadro de la carpeta, el balance, la exposición y la calidad, y la
/// carpeta entra a la biblioteca como un video (revelado por modulo-raw en Trabajos, con avance y cancelar).
/// </summary>
internal static class CinemaDngDialog
{
    public const string Title = "Importar CinemaDNG";

    public static async Task ShowAsync(EditorServices services, LongActionScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        EditorSession session = services.Session;
        Project project = session.Project;
        if (services.Modules?.FindByFunction(CinemaDngImport.Function) is not { } module)
        {
            session.Notify(NotificationSeverity.Informational, Title, "Hace falta el módulo «RAW de cine». Instálalo desde Complementos › Módulos de IA.");
            return;
        }

        if (services.Shell.XamlRoot is not { } root)
        {
            return;
        }

        nint owner = root.ContentIslandEnvironment is { } environment ? Win32Interop.GetWindowFromWindowId(environment.AppWindowId) : 0;
        IReadOnlyList<string> picked = FileDialogs.Open(owner, "Elige un fotograma de la carpeta CinemaDNG", multiple: false,
            FileDialogFolderPolicy.OpenFolder(services.Settings.Current), ("CinemaDNG", "*.dng"), FileDialogs.AllFilter);
        FileDialogFolderPolicy.RememberOpen(services.Settings.Current, picked.Count > 0 ? picked[0] : null);
        if (picked.Count == 0)
        {
            return;
        }

        CinemaDngFolder? sequence = await Task.Run(() => CinemaDngImport.Scan(picked[0]));
        if (sequence is null)
        {
            session.Notify(NotificationSeverity.Informational, Title, "La carpeta no tiene fotogramas .dng.");
            return;
        }

        (FfmpegExecutable? ffmpeg, string? reason) = await Task.Run(() => (FfmpegExecutable.TryLocate(out string? why), why));
        if (ffmpeg is null)
        {
            session.Notify(NotificationSeverity.Error, Title, reason ?? "No se encontró el ffmpeg.exe LGPL.");
            return;
        }

        var panel = new StackPanel { Spacing = 8, Width = AnalysisUi.PreviewWidth };
        panel.Children.Add(new TextBlock
        {
            Text = string.Create(SpanishNumbers.Format, $"«{sequence.Name}»: {sequence.Frames.Count} fotogramas")
                + (sequence.Audio is null ? "." : $", con el sonido de «{Path.GetFileName(sequence.Audio)}»."),
            TextWrapping = TextWrapping.Wrap,
        });
        var balance = new ComboBox { Header = "Balance de blancos", Width = AnalysisUi.PreviewWidth, SelectedIndex = 0 };
        balance.Items.Add("El de la toma");
        balance.Items.Add("Luz de día");
        panel.Children.Add(balance);
        double exposure = 0;
        Slider? slider = null;
        slider = AnalysisUi.Slider(panel, "Exposición", -3, 3, 0, 0.1,
            v => string.Create(SpanishNumbers.Format, $"{v:+0.0;−0.0;0} EV"), () => exposure = slider!.Value);
        var quality = new ComboBox { Header = "Calidad del revelado", Width = AnalysisUi.PreviewWidth, SelectedIndex = 0 };
        quality.Items.Add("Alta (más nítido, más lento)");
        quality.Items.Add("Rápida (para revisar)");
        panel.Children.Add(quality);
        panel.Children.Add(new TextBlock
        {
            Text = "Se guarda una copia ProRes 422 HQ; los DNG no cambian. Para revelar con otros ajustes, vuelve a importar.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.8,
        });
        var saveIn = new GeneratedFolderRow(services);
        panel.Children.Add(saveIn.Element);

        ContentDialog dialog = AnalysisUi.Dialog(root, Title, "Importar", panel);
        dialog.IsPrimaryButtonEnabled = true;
        if (await DialogGate.ShowAsync(dialog) != ContentDialogResult.Primary)
        {
            return;
        }

        services.Settings.Current.LastMediaFolder = sequence.Folder;
        var options = new CinemaDngOptions(balance.SelectedIndex == 1 ? "luz-dia" : "camara", exposure, quality.SelectedIndex == 0);
        scope.Report(null, $"Revelando «{sequence.Name}»…");
        CinemaDngResult result;
        try
        {
            result = await CinemaDngImport.RunAsync(services.Modules, module, ffmpeg, sequence, options,
                saveIn.Folder, $"CinemaDNG «{sequence.Name}»", scope.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (ModuleException)
        {
            // El ModuleManager ya lo avisó con un texto para la persona.
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            session.Notify(NotificationSeverity.Error, Title, "No se pudo importar: " + ex.Message);
            return;
        }

        // Un gesto en curso (arrastre) haría que ImportMediaAsync descartara el video en silencio: se espera a que acabe.
        while (session.IsInGesture && ReferenceEquals(session.Project, project))
        {
            await Task.Delay(100, scope.Token);
        }

        if (!ReferenceEquals(session.Project, project))
        {
            session.Notify(NotificationSeverity.Informational, Title, $"Se abrió otro proyecto mientras se revelaba; el video quedó en «{result.Path}».");
            return;
        }

        ImportResult imported = await session.ImportMediaAsync([result.Path], scope.Token);
        if (imported.MediaIds.Count > 0)
        {
            session.Notify(NotificationSeverity.Success, Title, string.Create(SpanishNumbers.Format,
                $"«{sequence.Name}»: {result.Frames} fotogramas de {result.Width}×{result.Height} a {(double)result.RateNumerator / result.RateDenominator:0.###} cuadros/s")
                + (result.Camera.Length > 0 ? $" ({result.Camera})." : "."));
        }
    }
}
