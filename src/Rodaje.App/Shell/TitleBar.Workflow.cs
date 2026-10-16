using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Rodaje.App.Services.Crashes;
using Rodaje.App.Services.Settings;

namespace Rodaje.App.Shell;

/// <summary>Los menús del flujo de trabajo: proxies al importar y espacios de trabajo.</summary>
internal sealed partial class TitleBar
{
    /// <summary>Se eligió un espacio de trabajo (su id).</summary>
    public event EventHandler<string>? WorkspaceChosen;

    /// <summary>«Guardar el actual como…».</summary>
    public event EventHandler? SaveWorkspaceRequested;

    /// <summary>Rehace el submenú de espacios de trabajo (tras guardar o quitar uno).</summary>
    public void RefreshWorkspaces() => UpdateWorkflowChecks();

    private void UpdateWorkflowChecks()
    {
        if (_services is null)
        {
            return;
        }

        AppSettings settings = _services.Settings.Current;
        AiQualityAuto.IsChecked = settings.AiQuality == Rodaje.Media.Ai.AiQuality.Auto;
        AiQualityFast.IsChecked = settings.AiQuality == Rodaje.Media.Ai.AiQuality.Fast;
        AiQualityMax.IsChecked = settings.AiQuality == Rodaje.Media.Ai.AiQuality.Max;
        WorkspaceMenu.Items.Clear();
        foreach (WorkspaceProfile profile in WorkspaceProfiles.All(settings))
        {
            var item = new RadioMenuFlyoutItem { Text = profile.Name, GroupName = "Workspace", Tag = profile.Id, IsChecked = profile.Id == settings.Workspace };
            item.Click += (_, _) => WorkspaceChosen?.Invoke(this, profile.Id);
            WorkspaceMenu.Items.Add(item);
        }

        WorkspaceMenu.Items.Add(new MenuFlyoutSeparator());
        var save = new MenuFlyoutItem { Text = "Guardar la distribución actual como..." };
        save.Click += (_, _) => SaveWorkspaceRequested?.Invoke(this, EventArgs.Empty);
        WorkspaceMenu.Items.Add(save);
        if (settings.Workspaces.Count > 0)
        {
            var remove = new MenuFlyoutSubItem { Text = "Quitar un espacio propio" };
            foreach (WorkspaceProfile own in settings.Workspaces)
            {
                var item = new MenuFlyoutItem { Text = own.Name };
                item.Click += (_, _) => UiTask.Run(RemoveWorkspaceAsync(own));
                remove.Items.Add(item);
            }

            WorkspaceMenu.Items.Add(remove);
        }
    }

    private async Task RemoveWorkspaceAsync(WorkspaceProfile profile)
    {
        if (_services is not { } services || DialogGate.IsOpen || XamlRoot is not { } root
            || !services.Settings.Current.Workspaces.Any(p => p.Id == profile.Id))
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "¿Quitar el espacio de trabajo?",
            Content = $"Se quitará «{profile.Name}» de tus espacios propios. No se puede deshacer.",
            PrimaryButtonText = "Quitar espacio",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Close,
            RequestedTheme = ElementTheme.Dark,
        };
        if (await DialogGate.ShowAsync(dialog) == ContentDialogResult.Primary
            && services.Settings.Current.Workspaces.Any(p => p.Id == profile.Id))
        {
            WorkspaceProfiles.Remove(services.Settings.Current, profile.Id);
            UpdateWorkflowChecks();
        }
    }

    private void OnAiQualityClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && _services is not null && Enum.TryParse(tag, out Rodaje.Media.Ai.AiQuality quality))
        {
            _services.Settings.Current.AiQuality = quality;
        }
    }

}
