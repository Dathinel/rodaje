using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
        AutoProxiesItem.IsChecked = settings.AutoProxies;
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
                item.Click += (_, _) =>
                {
                    WorkspaceProfiles.Remove(settings, own.Id);
                    UpdateWorkflowChecks();
                };
                remove.Items.Add(item);
            }

            WorkspaceMenu.Items.Add(remove);
        }
    }

    private void OnAiQualityClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && _services is not null && Enum.TryParse(tag, out Rodaje.Media.Ai.AiQuality quality))
        {
            _services.Settings.Current.AiQuality = quality;
        }
    }

    private void OnAutoProxiesClick(object sender, RoutedEventArgs e)
    {
        if (_services is not null)
        {
            _services.Settings.Current.AutoProxies = AutoProxiesItem.IsChecked;
        }
    }
}
