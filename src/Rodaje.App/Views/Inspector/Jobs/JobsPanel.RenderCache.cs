using Microsoft.UI.Xaml;
using Rodaje.App.Services.RenderCache;
using Rodaje.App.Shell;

namespace Rodaje.App.Views.Inspector.Jobs;

internal sealed partial class JobsPanel
{
    private RenderCacheHost? _renderCache;
    private int _activeRenderCache;

    private void AttachRenderCache(EditorServices services)
    {
        if (services.RenderCache is not { } cache)
        {
            return;
        }

        _renderCache = cache;
        cache.StatusChanged += (_, _) => _dispatcher.RunLater(ShowRenderCache);
        ShowRenderCache();
    }

    private void ShowRenderCache()
    {
        if (_renderCache is not { } cache)
        {
            return;
        }

        RenderCacheStatus status = cache.Status;
        bool busy = status.Current is not null || status.Pending > 0;
        RenderCacheRow.Visibility = busy || status.Completed > 0 || status.Failed > 0 ? Visibility.Visible : Visibility.Collapsed;
        RenderCacheText.Text = RenderCacheStatusText.Describe(status, cache.StripRate);
        RenderCacheProgress.Value = status.Progress * 100;
        RenderCacheProgress.Visibility = status.Current is null ? Visibility.Collapsed : Visibility.Visible;
        RenderCacheCancel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        int active = status.Current is null ? 0 : 1;
        if (active != _activeRenderCache)
        {
            _activeRenderCache = active;
            PublishActiveCount();
        }
    }

    private void OnRenderCacheCancelClick(object sender, RoutedEventArgs e) => _renderCache?.Cancel();
}
