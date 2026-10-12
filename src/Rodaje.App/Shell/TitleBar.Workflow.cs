using Microsoft.UI.Xaml;

namespace Rodaje.App.Shell;

/// <summary>Los menús del flujo de trabajo: proxies al importar.</summary>
internal sealed partial class TitleBar
{
    private void UpdateWorkflowChecks()
    {
        if (_services is null)
        {
            return;
        }

        AutoProxiesItem.IsChecked = _services.Settings.Current.AutoProxies;
    }

    private void OnAutoProxiesClick(object sender, RoutedEventArgs e)
    {
        if (_services is not null)
        {
            _services.Settings.Current.AutoProxies = AutoProxiesItem.IsChecked;
        }
    }
}
