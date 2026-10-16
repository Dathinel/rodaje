using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using Rodaje.App.Views.Analysis;
using Rodaje.App.Services;
using Rodaje.Core.Model;

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

        EditorSession session = services.Session;
        string[] selection = [.. session.SelectedClipIds];
        bool hasAudio = selection.Any(id => session.Sequence.FindClip(id) is { } found
            && found.Clip.Generator is null && session.Project.FindMedia(found.Clip.MediaId) is { HasAudio: true });
        bool hasVideo = selection.Any(id => session.Sequence.FindClip(id) is { } found
            && found.Clip.Generator is null && session.Project.FindMedia(found.Clip.MediaId) is { HasVideo: true, FrameRate: not null });
        IReadOnlyList<string> actionIds = QuickTools.ContextMenuActionIds(hasAudio, hasVideo);
        if (actionIds.Count == 0)
        {
            return;
        }

        menu.Items.Add(new MenuFlyoutSeparator());
        var actions = AnalysisCommands.Menu.Concat(Vision.VisionCommands.Menu)
            .Append((Id: AnalysisCommands.ReduceNoise, Label: "Reducir ruido…", Glyph: QuickTools.All.Single(tool => tool.ActionId == AnalysisCommands.ReduceNoise).Glyph))
            .ToDictionary(action => action.Id, StringComparer.Ordinal);
        bool noiseModule = services.Modules?.FindByFunction(NoiseReduction.Function) is not null;
        bool visionModule = services.Modules?.FindByFunction(Vision.VisionTools.RemoveBackgroundFunction) is not null;
        foreach (string id in actionIds)
        {
            (string _, string label, string glyph) = actions[id];
            QuickToolState state = QuickTools.State(id, session.Project, session.Sequence, selection, noiseModule, visionModule);
            var item = new MenuFlyoutItem { Text = label, Icon = new FontIcon { Glyph = glyph }, IsEnabled = state.Available };
            if (!state.Available && state.Reason is { } reason)
            {
                ToolTipService.SetToolTip(item, reason);
            }

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
        var translate = new MenuFlyoutItem { Text = Translate.TranslateCommands.Label, Icon = new FontIcon { Glyph = Translate.TranslateCommands.Glyph } };
        translate.Click += (_, _) => services.Shortcuts.Invoke(Translate.TranslateCommands.TranslateTrack);
        menu.Items.Add(translate);
    }
}
