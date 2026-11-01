namespace Rodaje.App.Services.Shortcuts;

/// <summary>Todos los cambios de atajos en un momento: teclas y ratón de cada estilo.</summary>
public sealed record ShortcutSnapshot(
    IReadOnlyDictionary<ShortcutPreset, IReadOnlyDictionary<string, IReadOnlyList<KeyChord>>> Keys,
    IReadOnlyDictionary<ShortcutPreset, IReadOnlyDictionary<MouseContext, IReadOnlyDictionary<MouseChord, string?>>> Mouse)
{
    public bool IsEmpty => Keys.Values.All(k => k.Count == 0) && Mouse.Values.All(m => m.Count == 0);
}
