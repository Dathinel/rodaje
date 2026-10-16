namespace Rodaje.App.Tests.Inspector;

public sealed class DestructiveActionSafetyTests
{
    [Fact]
    public void BorrarFormaDeMascara_PideConfirmacionAntesDeQuitarLosKeyframes()
    {
        string source = ReadSource("src", "Rodaje.App", "Views", "Inspector", "Masks", "MasksPanel.xaml.cs");
        string action = Between(source, "private async Task OnClearKeysClickAsync()", "private void OnViewClick");

        Assert.Contains("DialogGate.ShowAsync(dialog)", action, StringComparison.Ordinal);
        Assert.Contains("DefaultButton = ContentDialogButton.Close", action, StringComparison.Ordinal);
        Assert.Contains("CloseButtonText = \"Cancelar\"", action, StringComparison.Ordinal);
        Assert.Contains("!= ContentDialogResult.Primary", action, StringComparison.Ordinal);
        Assert.Contains("Run(Edits.ClearMaskShapeKeys", action, StringComparison.Ordinal);
        Assert.True(action.IndexOf("DialogGate.ShowAsync(dialog)", StringComparison.Ordinal)
            < action.IndexOf("Run(Edits.ClearMaskShapeKeys", StringComparison.Ordinal));
    }

    [Fact]
    public void QuitarPreajusteYEspacio_PidenConfirmacionCancelable()
    {
        string export = ReadSource("src", "Rodaje.App", "Views", "Export", "ExportView.xaml.cs");
        string preset = Between(export, "private async Task OnRemovePresetClickAsync()", "private void OnCancelClick");
        Assert.Contains("Origin: ExportPresetOrigin.User", preset, StringComparison.Ordinal);
        Assert.Contains("DialogGate.ShowAsync(dialog)", preset, StringComparison.Ordinal);
        Assert.Contains("DefaultButton = ContentDialogButton.Close", preset, StringComparison.Ordinal);
        Assert.Contains("DialogGate.ShowAsync(dialog) == ContentDialogResult.Primary", preset, StringComparison.Ordinal);
        Assert.Contains("model.RemovePreset(preset.Id)", preset, StringComparison.Ordinal);

        string workflow = ReadSource("src", "Rodaje.App", "Shell", "TitleBar.Workflow.cs");
        string workspace = Between(workflow, "private async Task RemoveWorkspaceAsync", "private void OnAiQualityClick");
        Assert.Contains("DialogGate.ShowAsync(dialog)", workspace, StringComparison.Ordinal);
        Assert.Contains("DefaultButton = ContentDialogButton.Close", workspace, StringComparison.Ordinal);
        Assert.Contains("DialogGate.ShowAsync(dialog) == ContentDialogResult.Primary", workspace, StringComparison.Ordinal);
        Assert.Contains("WorkspaceProfiles.Remove", workspace, StringComparison.Ordinal);
    }

    private static string Between(string source, string start, string end)
    {
        int first = source.IndexOf(start, StringComparison.Ordinal);
        int last = source.IndexOf(end, first + start.Length, StringComparison.Ordinal);
        Assert.True(first >= 0 && last > first, $"No se encontró el bloque {start} … {end}.");
        return source[first..last];
    }

    private static string ReadSource(params string[] parts)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string path = Path.Combine([directory.FullName, .. parts]);
            if (File.Exists(path))
            {
                return File.ReadAllText(path);
            }
        }

        throw new DirectoryNotFoundException($"No se encontró {Path.Combine(parts)} desde {AppContext.BaseDirectory}.");
    }
}
