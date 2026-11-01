namespace Rodaje.App.Services.Shortcuts;

/// <summary>
/// Las operaciones de la página de atajos sobre <see cref="ShortcutRegistry"/> y su ratón: asignar (con intercambio o
/// quitándosela a la acción que la tenía), quitar, restablecer una tecla, un botón, una zona o todo el estilo, e
/// importar. Cada una devuelve un aviso que se puede deshacer, y la última se deshace con <see cref="UndoLast"/>.
/// </summary>
public sealed class ShortcutCustomizer
{
    private readonly ShortcutRegistry _registry;
    private readonly Func<KeyChord, string> _keyText;
    private readonly List<ShortcutChange> _history = [];

    /// <param name="keyText">Cómo se escribe una tecla en los avisos; por defecto, con la distribución activa.</param>
    public ShortcutCustomizer(ShortcutRegistry registry, Func<KeyChord, string>? keyText = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _registry = registry;
        _keyText = keyText ?? KeyNames.Format;
    }

    /// <summary>Hay algún cambio que se puede deshacer con <see cref="UndoLast"/>.</summary>
    public bool CanUndo => _history.Count > 0;

    /// <summary>La acción que ya tiene esa combinación, sin contar <paramref name="except"/>.</summary>
    public string? OwnerOf(KeyChord chord, string? except = null) => _registry.FindOwner(chord, except);

    /// <summary>
    /// Asigna <paramref name="chord"/> a la acción, en lugar de <paramref name="replacing"/> o además de sus teclas. Si
    /// otra acción la tenía, según <paramref name="choice"/> recibe a cambio la reemplazada o la pierde. Las
    /// combinaciones reservadas no se asignan.
    /// </summary>
    public ShortcutChange Assign(string actionId, KeyChord chord, KeyChord? replacing = null, ConflictChoice choice = ConflictChoice.Swap)
    {
        string mine = LabelOf(actionId);
        string key = _keyText(chord);
        if (ReservedKeys.Reason(chord) is { } reason)
        {
            return Unchanged(reason);
        }

        if (_registry.GetKeys(actionId).Contains(chord) && (replacing is null || replacing == chord))
        {
            return Unchanged($"«{key}» ya era una tecla de «{mine}».");
        }

        ShortcutSnapshot before = _registry.TakeSnapshot();
        RebindResult result = _registry.Rebind(actionId, chord, replacing, swap: choice == ConflictChoice.Swap);
        if (!result.Changed)
        {
            return Unchanged($"«{key}» ya era una tecla de «{mine}».");
        }

        string message;
        if (result.TakenFromActionId is { } other)
        {
            string theirs = LabelOf(other);
            message = result.GivenToOther is not null
                ? $"«{key}» era de «{theirs}»: se intercambiaron"
                : $"«{key}» era de «{theirs}»; ahora es de «{mine}»";
        }
        else
        {
            message = replacing is null && _registry.GetKeys(actionId).Count > 1
                ? $"«{mine}» ahora también se lanza con «{key}»"
                : $"«{mine}» ahora se lanza con «{key}»";
        }

        return Record(message, before, ReservedKeys.Warning(chord));
    }

    /// <summary>
    /// Desde el teclado dibujado: la combinación pasa a lanzar <paramref name="actionId"/>. Si era de otra acción, con
    /// <see cref="ConflictChoice.Swap"/> esa otra se queda con la primera tecla que tenía <paramref name="actionId"/>.
    /// </summary>
    public ShortcutChange AssignToKey(KeyChord chord, string actionId, ConflictChoice choice = ConflictChoice.Swap)
    {
        KeyChord? replacing = choice == ConflictChoice.Swap && OwnerOf(chord, actionId) is not null && _registry.GetKeys(actionId) is [var first, ..]
            ? first
            : null;
        return Assign(actionId, chord, replacing, choice);
    }

    /// <summary>La combinación deja de lanzar acciones.</summary>
    public ShortcutChange ClearKey(KeyChord chord) =>
        OwnerOf(chord) is { } owner ? RemoveKey(owner, chord) : Unchanged($"«{_keyText(chord)}» ya no lanzaba nada.");

    public ShortcutChange RemoveKey(string actionId, KeyChord chord)
    {
        ShortcutSnapshot before = _registry.TakeSnapshot();
        if (!_registry.RemoveKey(actionId, chord))
        {
            return Unchanged("");
        }

        string message = $"Se quitó «{_keyText(chord)}» de «{LabelOf(actionId)}»";
        if (_registry.GetKeys(actionId).Count == 0)
        {
            message += "; ya no tiene tecla";
        }

        return Record(message, before);
    }

    public ShortcutChange Reset(string actionId)
    {
        if (!_registry.IsCustomized(actionId))
        {
            return Unchanged("");
        }

        ShortcutSnapshot before = _registry.TakeSnapshot();
        _registry.Reset(actionId);
        return Record($"«{LabelOf(actionId)}» volvió a sus teclas de fábrica", before);
    }

    /// <summary>
    /// Todas las combinaciones de una tecla física (sola, con Ctrl, con Mayús…) vuelven a las de fábrica; las demás
    /// teclas de esas acciones no se tocan.
    /// </summary>
    public ShortcutChange ResetKey(int virtualKey)
    {
        string name = KeyNames.KeyName(virtualKey);
        var changes = new List<(string Id, List<KeyChord> Keys)>();
        foreach (ShortcutAction action in _registry.Actions)
        {
            IReadOnlyList<KeyChord> current = _registry.GetKeys(action.Id);
            IReadOnlyList<KeyChord> factory = action.DefaultKeys(_registry.Preset);
            List<KeyChord> mine = [.. current.Where(k => k.Key != virtualKey)];
            mine.AddRange(factory.Where(k => k.Key == virtualKey));
            if (!mine.ToHashSet().SetEquals(current))
            {
                // Se respeta el orden de fábrica cuando todo vuelve a ser como de fábrica.
                changes.Add((action.Id, mine.ToHashSet().SetEquals(factory) ? [.. factory] : mine));
            }
        }

        if (changes.Count == 0)
        {
            return Unchanged($"«{name}» ya hace lo de fábrica.");
        }

        ShortcutSnapshot before = _registry.TakeSnapshot();
        var overrides = new Dictionary<string, IReadOnlyList<KeyChord>>(_registry.GetOverrides(_registry.Preset), StringComparer.Ordinal);
        foreach ((string id, List<KeyChord> keys) in changes)
        {
            if (_registry.Find(id)!.DefaultKeys(_registry.Preset).SequenceEqual(keys))
            {
                overrides.Remove(id);
            }
            else
            {
                overrides[id] = keys;
            }
        }

        _registry.SetOverrides(_registry.Preset, overrides);
        return Record($"«{name}» volvió a sus acciones de fábrica", before);
    }

    /// <summary>Todo el estilo (teclas y ratón) vuelve a lo de fábrica.</summary>
    public ShortcutChange ResetAll()
    {
        ShortcutPreset preset = _registry.Preset;
        if (_registry.GetOverrides(preset).Count == 0 && !_registry.Mouse.HasOverrides(preset))
        {
            return Unchanged($"El estilo {ShortcutPresetNames.Display(preset)} ya tiene sus teclas de fábrica.");
        }

        ShortcutSnapshot before = _registry.TakeSnapshot();
        _registry.ResetAll();
        foreach (MouseContext context in Enum.GetValues<MouseContext>())
        {
            _registry.Mouse.ResetContext(context);
        }

        return Record($"El estilo {ShortcutPresetNames.Display(preset)} volvió a sus teclas de fábrica", before);
    }

    /// <summary>Pone una acción en una combinación del ratón (ver <see cref="MouseMap.Assign"/>).</summary>
    public ShortcutChange AssignMouse(MouseContext context, MouseChord chord, string actionId, MouseChord? from = null, ConflictChoice choice = ConflictChoice.Swap)
    {
        if (_registry.Mouse.Reason(context, chord, actionId) is { } reason)
        {
            return Unchanged(reason);
        }

        string mine = LabelOf(actionId);
        string where = $"«{chord}» ({MouseNames.Display(context).ToLowerInvariant()})";
        if (_registry.Mouse.Get(context, chord) == actionId)
        {
            return Unchanged($"{where} ya hacía «{mine}».");
        }

        ShortcutSnapshot before = _registry.TakeSnapshot();
        MouseRebind result = _registry.Mouse.Assign(context, chord, actionId, from, choice == ConflictChoice.Swap);
        string message = result.PreviousOwner is { } other
            ? result.SwappedTo is { } swapped
                ? $"{where} era de «{LabelOf(other)}»: se intercambiaron y «{LabelOf(other)}» pasó a «{swapped}»"
                : $"{where} era de «{LabelOf(other)}»; ahora hace «{mine}»"
            : $"{where} ahora hace «{mine}»";
        return Record(message, before);
    }

    public ShortcutChange RemoveMouse(MouseContext context, MouseChord chord)
    {
        string? owner = _registry.Mouse.Get(context, chord);
        ShortcutSnapshot before = _registry.TakeSnapshot();
        if (owner is null || !_registry.Mouse.Remove(context, chord))
        {
            return Unchanged("");
        }

        return Record($"«{chord}» ya no hace «{LabelOf(owner)}» en {MouseNames.Display(context).ToLowerInvariant()}", before);
    }

    /// <summary>Un botón o eje del ratón vuelve a lo de fábrica (con todos sus modificadores).</summary>
    public ShortcutChange ResetMouse(MouseContext context, MouseInput input)
    {
        ShortcutSnapshot before = _registry.TakeSnapshot();
        return _registry.Mouse.Reset(context, input)
            ? Record($"«{MouseNames.Display(input)}» volvió a lo de fábrica en {MouseNames.Display(context).ToLowerInvariant()}", before)
            : Unchanged($"«{MouseNames.Display(input)}» ya hace lo de fábrica.");
    }

    /// <summary>Todo el ratón de una zona vuelve a lo de fábrica.</summary>
    public ShortcutChange ResetMouseContext(MouseContext context)
    {
        ShortcutSnapshot before = _registry.TakeSnapshot();
        return _registry.Mouse.ResetContext(context)
            ? Record($"El ratón de {MouseNames.Display(context).ToLowerInvariant()} volvió a lo de fábrica", before)
            : Unchanged($"El ratón de {MouseNames.Display(context).ToLowerInvariant()} ya hace lo de fábrica.");
    }

    /// <summary>Lee un mapa exportado y reemplaza los cambios de todos los estilos; se puede deshacer.</summary>
    public ShortcutChange Import(string path)
    {
        ShortcutSnapshot before = _registry.TakeSnapshot();
        if (ShortcutStore.Import(_registry, path) is { } error)
        {
            return Unchanged(error);
        }

        return Record($"Se importaron los atajos de {Path.GetFileName(path)}", before, allPresets: true);
    }

    /// <summary>Deja los cambios del estilo como estaban antes de <paramref name="change"/>.</summary>
    public bool Undo(ShortcutChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (change.Before is not { } before)
        {
            return false;
        }

        _registry.Restore(before, change.AllPresets ? null : change.Preset);
        int at = _history.IndexOf(change);
        if (at >= 0)
        {
            _history.RemoveRange(at, _history.Count - at);
        }

        return true;
    }

    /// <summary>Deshace el último cambio; devuelve el que se deshizo o <c>null</c>.</summary>
    public ShortcutChange? UndoLast()
    {
        if (_history.Count == 0)
        {
            return null;
        }

        ShortcutChange last = _history[^1];
        Undo(last);
        return last;
    }

    /// <summary>Las otras acciones que usan esas teclas de la acción (conflictos con las que registran las vistas).</summary>
    public IReadOnlyList<string> ConflictsOf(string actionId)
    {
        var others = new List<string>();
        foreach (ShortcutConflict conflict in _registry.FindConflicts())
        {
            if (conflict.ActionIds.Contains(actionId))
            {
                others.AddRange(conflict.ActionIds.Where(id => id != actionId && !others.Contains(id)));
            }
        }

        return others;
    }

    /// <summary>El nombre de una acción del teclado o del ratón.</summary>
    public string LabelOf(string actionId) => MouseGestures.Find(actionId)?.Label ?? _registry.Find(actionId)?.Label ?? actionId;

    private ShortcutChange Unchanged(string message) => new(message, _registry.Preset, null);

    private ShortcutChange Record(string message, ShortcutSnapshot before, string? warning = null, bool allPresets = false)
    {
        var change = new ShortcutChange(message, _registry.Preset, before, warning, allPresets);
        _history.Add(change);
        if (_history.Count > 50)
        {
            _history.RemoveAt(0);
        }

        return change;
    }
}
