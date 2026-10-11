namespace Rodaje.App.Services.Shortcuts;

/// <summary>
/// Los atajos de la vista esencial de la página de atajos: lo justo para editar de corrido. La vista avanzada muestra
/// todos y permite cambiarlos.
/// </summary>
public static class EssentialShortcuts
{
    /// <summary>En el orden en que se muestran.</summary>
    public static IReadOnlyList<string> Ids { get; } =
    [
        ActionIds.TogglePlay,
        ActionIds.ShuttleReverse,
        ActionIds.ShuttleStop,
        ActionIds.ShuttleForward,
        ActionIds.PreviousFrame,
        ActionIds.NextFrame,
        ActionIds.GoToStart,
        ActionIds.GoToEnd,
        ActionIds.Split,
        ActionIds.Delete,
        ActionIds.ToggleAutoRipple,
        ActionIds.Undo,
        ActionIds.Redo,
        ActionIds.AddMarker,
        ActionIds.ZoomIn,
        ActionIds.ZoomOut,
        ActionIds.Save,
        ActionIds.Import,
    ];

    public static bool Contains(ShortcutAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return Ids.Contains(action.Id);
    }

    /// <summary>Las esenciales registradas y con tecla en el estilo actual, cada una con dos teclas como mucho.</summary>
    public static IReadOnlyList<CheatSheetEntry> Select(ShortcutRegistry registry, Func<KeyChord, string>? keyText = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        keyText ??= KeyNames.Format;
        var entries = new List<CheatSheetEntry>();
        foreach (string id in Ids)
        {
            if (registry.Find(id) is { } action && registry.GetKeys(id) is { Count: > 0 } keys)
            {
                entries.Add(new CheatSheetEntry(id, action.Label, action.Group, [.. keys.Take(2).Select(keyText)]));
            }
        }

        return entries;
    }
}
