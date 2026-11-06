using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Rodaje.App.Services;
using Rodaje.App.Services.Settings;
using Rodaje.App.Services.Shortcuts;

namespace Rodaje.App.Shell;

/// <summary>
/// Ayuda › Hoja de atajos (imprimible)…: todos los atajos del estilo actual en un HTML, donde diga el diálogo de guardar
/// (Documentos la primera vez; después, la última carpeta elegida para las hojas y los mapas de atajos).
/// </summary>
#pragma warning disable CA1001 // Lo descartable se libera en OnClosed (MainWindow.xaml.cs).
internal sealed partial class MainWindow
#pragma warning restore CA1001
{
    private async Task SaveShortcutSheetAsync()
    {
        string? path;
        try
        {
            path = FileDialogs.Save(_hwnd, "Guardar la hoja de atajos", SheetFiles.FullSheetName, "html",
                FileDialogFolderPolicy.SaveFolder(_settings.Current, SaveDialogKind.Shortcuts), SheetFiles.HtmlFilter);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            Session.Notify(NotificationSeverity.Warning, "Hoja de atajos", $"No se pudo abrir el diálogo: {ex.Message}");
            return;
        }

        if (path is null)
        {
            return;
        }

        FileDialogFolderPolicy.RememberSave(_settings.Current, SaveDialogKind.Shortcuts, path);
        ShortcutRegistry registry = _router.Registry;
        string preset = ShortcutPresetNames.Display(registry.Preset);
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
        string name = Path.GetFileName(path);
        string folder = Path.GetDirectoryName(path) ?? path;
        Session.Notify(NotificationSeverity.Success, "Hoja de atajos guardada", $"«{name}» está en «{folder}». Ábrela en el navegador e imprímela.", open);
    }
}
