using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Rodaje.App.Services;
using Rodaje.App.Services.Shortcuts;

namespace Rodaje.App.Shell;

/// <summary>Ayuda › Hoja de atajos (imprimible): todos los atajos del estilo actual en un HTML en Documentos.</summary>
internal sealed partial class MainWindow
{
    private const string SheetFileName = "Atajos de Rodaje (completa).html";

    private async Task SaveShortcutSheetAsync()
    {
        string folder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrEmpty(folder))
        {
            folder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        string path = Path.Combine(folder, SheetFileName);
        ShortcutRegistry registry = _router.Registry;
        string preset = registry.Preset switch
        {
            ShortcutPreset.Agile => "Ágil",
            ShortcutPreset.Classic => "Clásico",
            _ => "Rodaje",
        };
        string html = ShortcutGuide.ToHtml(registry, $"Estilo {preset}, con tus cambios · {DateTime.Now:d}");
        try
        {
            await File.WriteAllTextAsync(path, html, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Session.Notify(NotificationSeverity.Error, "No se pudo guardar la hoja", ex.Message);
            return;
        }

        var open = new NotificationAction("Abrir", () =>
        {
            try
            {
                using Process? _ = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                Session.Notify(NotificationSeverity.Warning, "Hoja de atajos", $"No se pudo abrir «{path}»: {ex.Message}");
            }
        });
        Session.Notify(NotificationSeverity.Success, "Hoja de atajos guardada", $"«{SheetFileName}» está en «{folder}». Ábrela en el navegador e imprímela.", open);
    }
}
