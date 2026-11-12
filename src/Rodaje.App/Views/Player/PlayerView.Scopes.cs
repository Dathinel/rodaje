using Microsoft.UI.Xaml;
using Rodaje.App.Shell;
using Rodaje.Render.Scopes;

namespace Rodaje.App.Views.Player;

/// <summary>El botón de monitores de la cabecera abre el panel junto al visor; lo elegido queda en <c>ajustes.json</c>.</summary>
internal sealed partial class PlayerView
{
    private void AttachScopes(EditorServices services)
    {
        Scopes.Attach(services.Player);
        var settings = services.Settings.Current;
        Scopes.Kind = Enum.TryParse(settings.Scope, out ScopeKind kind) ? kind : ScopeKind.Waveform;
        Scopes.CloseRequested += (_, _) => ShowScopes(false);
        Scopes.KindChanged += (_, _) =>
        {
            services.Settings.Current.Scope = Scopes.Kind.ToString();
            services.Settings.Save();
        };
        ShowScopes(settings.ShowScopes, save: false);
    }

    private void OnScopesClick(object sender, RoutedEventArgs e) => ShowScopes(ScopesButton.IsChecked == true);

    /// <summary>Abre o cierra el panel; la medición en la GPU solo corre con el panel abierto.</summary>
    internal void ShowScopes(bool show, bool save = true)
    {
        ScopesButton.IsChecked = show;
        Scopes.Visibility = show && !IsFullScreen ? Visibility.Visible : Visibility.Collapsed;
        Scopes.SetActive(show);
        if (save && _services is { } services)
        {
            services.Settings.Current.ShowScopes = show;
            services.Settings.Save();
        }
    }
}
