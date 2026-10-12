using Microsoft.UI.Xaml.Controls;
using Rodaje.App.Views.Analysis;

namespace Rodaje.App.Views.Timeline;

/// <summary>Acciones de análisis de audio dentro del menú contextual del clip.</summary>
internal sealed partial class TimelineView
{
    private void AddAnalysisItems(MenuFlyout menu)
    {
        if (_services is not { } services)
        {
            return;
        }

        menu.Items.Add(new MenuFlyoutSeparator());
        foreach ((string id, string label, string glyph) in AnalysisCommands.Menu)
        {
            var item = new MenuFlyoutItem { Text = label, Icon = new FontIcon { Glyph = glyph } };
            item.Click += (_, _) => services.Shortcuts.Invoke(id);
            menu.Items.Add(item);
        }
    }
}
