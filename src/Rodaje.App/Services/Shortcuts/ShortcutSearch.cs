namespace Rodaje.App.Services.Shortcuts;

/// <summary>
/// La búsqueda de la página de atajos. Sin distinguir mayúsculas ni tildes, una acción aparece si su nombre, su
/// explicación, su grupo o sus teclas contienen todas las palabras. Si lo escrito es una tecla de un carácter («S», «Ñ»)
/// o una combinación con «+» («ctrl+b»), se buscan solo las acciones con esa tecla (si ninguna la tiene, se busca como
/// texto); un nombre de tecla más largo («supr», «pausa») encuentra las dos cosas.
/// </summary>
public sealed class ShortcutSearch
{
    private readonly string[] _words;
    private readonly KeyChord? _chord;
    private bool _keysOnly;
    private readonly IKeyboardLayout? _layout;

    public ShortcutSearch(string? query, IKeyboardLayout? layout)
    {
        _layout = layout;
        string text = (query ?? "").Trim();
        _words = KeyNames.Fold(text).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (text.Length > 0 && KeyNames.TryParse(text, layout, out KeyChord chord))
        {
            _chord = chord;
            _keysOnly = text.Length == 1 || (text.Length > 1 && text.Contains('+', StringComparison.Ordinal));
        }
    }

    public bool IsEmpty => _words.Length == 0;

    /// <summary>La combinación que se escribió, si lo escrito se entiende como tecla.</summary>
    public KeyChord? Chord => _chord;

    public bool Matches(ShortcutAction action, IReadOnlyList<KeyChord> keys)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(keys);
        if (IsEmpty)
        {
            return true;
        }

        if (_chord is { } chord && keys.Contains(chord))
        {
            return true;
        }

        if (_keysOnly)
        {
            return false;
        }

        string keyText = string.Join(' ', keys.Select(k => KeyNames.Format(k, _layout)));
        string haystack = KeyNames.Fold($"{action.Label} {action.Description} {action.Group} {keyText}");
        return _words.All(w => haystack.Contains(w, StringComparison.Ordinal));
    }

    /// <summary>Las acciones del registro que coinciden, en su orden.</summary>
    public static IReadOnlyList<ShortcutAction> Filter(ShortcutRegistry registry, string? query, IKeyboardLayout? layout)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var search = new ShortcutSearch(query, layout);
        List<ShortcutAction> found = [.. registry.Actions.Where(a => search.Matches(a, registry.GetKeys(a.Id)))];
        if (found.Count == 0 && search._keysOnly)
        {
            search._keysOnly = false;
            found = [.. registry.Actions.Where(a => search.Matches(a, registry.GetKeys(a.Id)))];
        }

        return found;
    }
}
