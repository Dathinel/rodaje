namespace Rodaje.App.Services.Shortcuts;

/// <summary>Dos o más acciones con la misma combinación; la primera registrada es la que se lanza.</summary>
public sealed record ShortcutConflict(KeyChord Chord, IReadOnlyList<string> ActionIds);
