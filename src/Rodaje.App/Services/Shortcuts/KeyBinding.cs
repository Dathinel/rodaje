namespace Rodaje.App.Services.Shortcuts;

/// <summary>Una combinación y la acción que la usa.</summary>
public sealed record KeyBinding(KeyChord Chord, ShortcutAction Action);
