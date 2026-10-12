using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Rodaje.App.Services;
using Rodaje.App.Services.Settings;

namespace Rodaje.App.Shell;

/// <summary>Las preguntas del editor con diálogos reales: ContentDialog de WinUI y los de archivos de Windows.</summary>
internal sealed class ShellDialogs(Window window, nint hwnd, SettingsStore settings) : IEditorDialogs
{
    public async Task<UnsavedChangesChoice> AskSaveChangesAsync(string projectName)
    {
        if (window.Content?.XamlRoot is not { } root)
        {
            return UnsavedChangesChoice.Cancel;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "¿Guardar los cambios?",
            Content = $"«{projectName}» tiene cambios sin guardar. Si no los guardas, se pierden.",
            PrimaryButtonText = "Guardar",
            SecondaryButtonText = "No guardar",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Primary,
            RequestedTheme = ElementTheme.Dark,
        };
        return await DialogGate.ShowAsync(dialog) switch
        {
            ContentDialogResult.Primary => UnsavedChangesChoice.Save,
            ContentDialogResult.Secondary => UnsavedChangesChoice.Discard,
            _ => UnsavedChangesChoice.Cancel,
        };
    }

    public async Task<bool> ConfirmAsync(string title, string message, string confirmText)
    {
        if (window.Content?.XamlRoot is not { } root)
        {
            return false;
        }

        // Lo que no se deshace no se confirma con Intro: el botón por defecto es Cancelar.
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = confirmText,
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Close,
            RequestedTheme = ElementTheme.Dark,
        };
        return await DialogGate.ShowAsync(dialog) == ContentDialogResult.Primary;
    }

    public Task<string?> PickProjectToOpenAsync()
    {
        IReadOnlyList<string> picked = FileDialogs.Open(hwnd, "Abrir proyecto", multiple: false,
            settings.Current.LastProjectFolder, FileDialogs.ProjectFilter, FileDialogs.AllFilter);
        string? path = picked.Count > 0 ? picked[0] : null;
        Remember(path, project: true);
        return Task.FromResult(path);
    }

    public Task<string?> PickProjectSavePathAsync(string suggestedFileName)
    {
        string? path = FileDialogs.Save(hwnd, "Guardar proyecto", suggestedFileName, "rodaje",
            settings.Current.LastProjectFolder, FileDialogs.ProjectFilter);
        Remember(path, project: true);
        return Task.FromResult(path);
    }

    public Task<IReadOnlyList<string>> PickMediaFilesAsync()
    {
        IReadOnlyList<string> picked = FileDialogs.Open(hwnd, "Importar medios", multiple: true,
            settings.Current.LastMediaFolder, FileDialogs.MediaFilter, FileDialogs.AllFilter);
        Remember(picked.Count > 0 ? picked[0] : null, project: false);
        return Task.FromResult(picked);
    }

    private void Remember(string? path, bool project)
    {
        if (path is null || Path.GetDirectoryName(path) is not { } folder)
        {
            return;
        }

        if (project)
        {
            settings.Current.LastProjectFolder = folder;
        }
        else
        {
            settings.Current.LastMediaFolder = folder;
        }
    }
}
