using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Rodaje.App.Services.Shortcuts;

namespace Rodaje.App.Views.Shortcuts;

/// <summary>Color de cada grupo de atajos, tomado de la paleta: el mismo en la lista, el teclado y la hoja rápida.</summary>
internal static class GroupBrushes
{
    public static Brush For(string group) => Resource(group switch
    {
        ShortcutGroups.Playback => "AudioClipEdgeBrush",
        ShortcutGroups.Edit => "AccentBrush",
        ShortcutGroups.Timeline => "VideoClipEdgeBrush",
        ShortcutGroups.Tools => "TextClipEdgeBrush",
        ShortcutGroups.View => "KeyframeBrush",
        ShortcutGroups.File => "TextMutedBrush",
        MouseGestures.Group => "TextBrush",
        _ => "MeterHighBrush",
    });

    public static Brush Resource(string key) => (Brush)Application.Current.Resources[key];
}
