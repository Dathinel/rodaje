using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Rodaje.App.Services;
using Rodaje.App.Services.Media;
using Rodaje.App.Shell;

namespace Rodaje.App.Views.Library;

/// <summary>El aviso de medios nuevos en las carpetas vigiladas, al pie de la biblioteca.</summary>
internal sealed partial class LibraryView
{
    private void AttachWatched(EditorServices services)
    {
        if (services.WatchedFolders is { } inbox)
        {
            inbox.Changed += (_, _) => ShowWatched(inbox);
        }
    }

    private void ShowWatched(WatchedFolderInbox inbox)
    {
        WatchedBanner.Visibility = inbox.Pending.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (inbox.Pending.Count > 0)
        {
            WatchedText.Text = WatchedFolderInbox.Describe(inbox.Pending);
            ToolTipService.SetToolTip(WatchedText, string.Join("\n", inbox.Pending.Take(20).Select(f => Path.GetFileName(f.Path))));
        }
    }

    private async void OnWatchedImportClick(object sender, RoutedEventArgs e)
    {
        if (_services?.WatchedFolders is not { } inbox)
        {
            return;
        }

        try
        {
            await inbox.ImportAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _services.Session.Notify(NotificationSeverity.Error, "No se pudieron importar los medios nuevos", ex.Message);
        }
    }

    private void OnWatchedDismissClick(object sender, RoutedEventArgs e) => _services?.WatchedFolders?.Dismiss();
}
