namespace Rodaje.App.Services.Shortcuts;

/// <summary>Nombres de los estilos tal como se ven en la página de atajos.</summary>
public static class ShortcutPresetNames
{
    public static string Display(ShortcutPreset preset) => preset switch
    {
        ShortcutPreset.CapCut => "Como CapCut",
        ShortcutPreset.Vegas => "Como Vegas",
        _ => "Rodaje",
    };
}

/// <summary>
/// Un cambio de atajos hecho desde la página, con el aviso que se le muestra a la persona y lo necesario para
/// deshacerlo (<see cref="ShortcutCustomizer.Undo"/>).
/// </summary>
public sealed class ShortcutChange
{
    internal ShortcutChange(string message, ShortcutPreset preset, IReadOnlyDictionary<string, IReadOnlyList<KeyChord>>? before)
    {
        Message = message;
        Preset = preset;
        Before = before;
    }

    /// <summary>El aviso, en español: «"S" era de "Cortar en el cursor": se intercambiaron».</summary>
    public string Message { get; }

    /// <summary>Cambió algo y se puede deshacer.</summary>
    public bool Changed => Before is not null;

    public ShortcutPreset Preset { get; }

    internal IReadOnlyDictionary<string, IReadOnlyList<KeyChord>>? Before { get; }
}

/// <summary>
/// Las operaciones de la página de atajos sobre <see cref="ShortcutRegistry"/>: asignar una tecla (con intercambio si
/// otra acción la tenía), quitarla, restablecer una acción o todas. Cada una devuelve un aviso que se puede deshacer.
/// </summary>
public sealed class ShortcutCustomizer
{
    private readonly ShortcutRegistry _registry;
    private readonly Func<KeyChord, string> _keyText;

    /// <param name="keyText">Cómo se escribe una tecla en los avisos; por defecto, con la distribución activa.</param>
    public ShortcutCustomizer(ShortcutRegistry registry, Func<KeyChord, string>? keyText = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _registry = registry;
        _keyText = keyText ?? KeyNames.Format;
    }

    /// <summary>
    /// Asigna <paramref name="chord"/> a la acción, en lugar de <paramref name="replacing"/> o además de sus teclas. Las
    /// combinaciones reservadas no se asignan.
    /// </summary>
    public ShortcutChange Assign(string actionId, KeyChord chord, KeyChord? replacing = null)
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

        IReadOnlyDictionary<string, IReadOnlyList<KeyChord>> before = Snapshot();
        RebindResult result = _registry.Rebind(actionId, chord, replacing);
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

        return new ShortcutChange(message, _registry.Preset, before);
    }

    public ShortcutChange RemoveKey(string actionId, KeyChord chord)
    {
        IReadOnlyDictionary<string, IReadOnlyList<KeyChord>> before = Snapshot();
        if (!_registry.RemoveKey(actionId, chord))
        {
            return Unchanged("");
        }

        string message = $"Se quitó «{_keyText(chord)}» de «{LabelOf(actionId)}»";
        if (_registry.GetKeys(actionId).Count == 0)
        {
            message += "; ya no tiene tecla";
        }

        return new ShortcutChange(message, _registry.Preset, before);
    }

    public ShortcutChange Reset(string actionId)
    {
        if (!_registry.IsCustomized(actionId))
        {
            return Unchanged("");
        }

        IReadOnlyDictionary<string, IReadOnlyList<KeyChord>> before = Snapshot();
        _registry.Reset(actionId);
        return new ShortcutChange($"«{LabelOf(actionId)}» volvió a sus teclas de fábrica", _registry.Preset, before);
    }

    public ShortcutChange ResetAll()
    {
        IReadOnlyDictionary<string, IReadOnlyList<KeyChord>> before = Snapshot();
        if (before.Count == 0)
        {
            return Unchanged($"El estilo {ShortcutPresetNames.Display(_registry.Preset)} ya tiene sus teclas de fábrica.");
        }

        _registry.ResetAll();
        return new ShortcutChange(
            $"El estilo {ShortcutPresetNames.Display(_registry.Preset)} volvió a sus teclas de fábrica", _registry.Preset, before);
    }

    /// <summary>Deja los cambios del estilo como estaban antes de <paramref name="change"/>.</summary>
    public bool Undo(ShortcutChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (change.Before is not { } before)
        {
            return false;
        }

        _registry.SetOverrides(change.Preset, before);
        return true;
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

    private string LabelOf(string actionId) => _registry.Find(actionId)?.Label ?? actionId;

    private ShortcutChange Unchanged(string message) => new(message, _registry.Preset, null);

    private IReadOnlyDictionary<string, IReadOnlyList<KeyChord>> Snapshot() => _registry.GetOverrides(_registry.Preset);
}
