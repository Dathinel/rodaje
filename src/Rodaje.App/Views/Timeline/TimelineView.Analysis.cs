using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Rodaje.App.Views.Analysis;

namespace Rodaje.App.Views.Timeline;

/// <summary>Menú contextual del clip con las acciones de análisis de audio.</summary>
internal sealed partial class TimelineView
{
    private void ShowClipMenu(Windows.Foundation.Point at)
    {
        if (_services is not { } services)
        {
            return;
        }

        var menu = new MenuFlyout();
        foreach ((string id, string label, string glyph) in AnalysisCommands.Menu)
        {
            var item = new MenuFlyoutItem { Text = label, Icon = new FontIcon { Glyph = glyph } };
            item.Click += (_, _) => services.Shortcuts.Invoke(id);
            menu.Items.Add(item);
        }

        menu.ShowAt(LanesHost, new FlyoutShowOptions { Position = at });
    }
}
