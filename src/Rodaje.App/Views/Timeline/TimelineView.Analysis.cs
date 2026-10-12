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

        var voice = new MenuFlyoutItem { Text = Voice.VoiceCommands.Label, Icon = new FontIcon { Glyph = Voice.VoiceCommands.Glyph } };
        voice.Click += (_, _) => services.Shortcuts.Invoke(Voice.VoiceCommands.TextToSpeech);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(voice);
        var subtitles = new MenuFlyoutItem { Text = Subtitles.AutoSubtitlesDialog.Label, Icon = new FontIcon { Glyph = Subtitles.AutoSubtitlesDialog.Glyph } };
        subtitles.Click += (_, _) => services.Shortcuts.Invoke(Subtitles.SubtitleCommands.AutoSubtitles);
        menu.Items.Add(subtitles);
    }
}
