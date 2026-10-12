namespace Rodaje.App.Services.Shortcuts;

/// <summary>Dos o más acciones con la misma combinación; la primera registrada es la que se lanza.</summary>
public sealed record ShortcutConflict(KeyChord Chord, IReadOnlyList<string> ActionIds);

/// <summary>Qué pasó al asignar una tecla.</summary>
/// <param name="Changed">Cambió algo.</param>
/// <param name="TakenFromActionId">La acción que tenía la tecla y la perdió, si había una.</param>
/// <param name="GivenToOther">La tecla anterior de la acción, que pasó a <paramref name="TakenFromActionId"/> (intercambio).</param>
public sealed record RebindResult(bool Changed, string? TakenFromActionId, KeyChord? GivenToOther)
{
    public static readonly RebindResult None = new(false, null, null);
}

/// <summary>
/// Catálogo de acciones con teclado y sus teclas: las de fábrica del estilo elegido más los cambios de la persona, que
/// se guardan por estilo. No ejecuta nada: eso lo hace <see cref="ShortcutRouter"/>.
/// </summary>
/// <remarks>
/// Cada vista registra sus acciones al crearse con <see cref="Register"/>. Los cambios guardados de una acción que
/// todavía no se registró se conservan y se aplican cuando llegue.
/// </remarks>
public sealed class ShortcutRegistry
{
    private readonly List<ShortcutAction> _actions = [];
    private readonly Dictionary<string, ShortcutAction> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<ShortcutPreset, Dictionary<string, KeyChord[]>> _overrides = [];
    private Dictionary<KeyChord, string>? _lookup;
    private ShortcutPreset _preset;

    public ShortcutRegistry() => Mouse = new MouseMap(this);

    /// <summary>Cambió el estilo, una tecla o el catálogo.</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<ShortcutAction> Actions => _actions;

    /// <summary>El ratón: qué hace cada botón y la rueda en cada zona, con los cambios de la persona.</summary>
    public MouseMap Mouse { get; }

    public ShortcutPreset Preset
    {
        get => _preset;
        set
        {
            if (!Enum.IsDefined(value) || value == _preset)
            {
                return;
            }

            _preset = value;
            OnChanged();
        }
    }

    public void Register(ShortcutAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (!_byId.TryAdd(action.Id, action))
        {
            throw new InvalidOperationException($"La acción «{action.Id}» ya está registrada.");
        }

        _actions.Add(action);
        OnChanged();
    }

    public void RegisterRange(IEnumerable<ShortcutAction> actions)
    {
        ArgumentNullException.ThrowIfNull(actions);
        foreach (ShortcutAction action in actions)
        {
            Register(action);
        }
    }

    public ShortcutAction? Find(string actionId) => _byId.GetValueOrDefault(actionId);

    /// <summary>Las teclas de la acción en el estilo actual, con los cambios de la persona.</summary>
    public IReadOnlyList<KeyChord> GetKeys(string actionId)
    {
        if (CurrentOverrides.TryGetValue(actionId, out KeyChord[]? custom))
        {
            return custom;
        }

        return _byId.TryGetValue(actionId, out ShortcutAction? action) ? action.DefaultKeys(_preset) : [];
    }

    /// <summary>La primera tecla en español, para menús y botones («Ctrl+Z»), o vacío.</summary>
    public string GetKeyText(string actionId) => GetKeys(actionId) is [var first, ..] ? first.ToString() : "";

    /// <summary>La persona cambió las teclas de esta acción en el estilo actual.</summary>
    public bool IsCustomized(string actionId) => CurrentOverrides.ContainsKey(actionId);

    /// <summary>La acción registrada que se lanza con esa combinación, o <c>null</c>.</summary>
    public string? FindAction(KeyChord chord)
    {
        _lookup ??= BuildLookup();
        return _lookup.GetValueOrDefault(chord);
    }

    /// <summary>Combinaciones repetidas entre acciones registradas.</summary>
    public IReadOnlyList<ShortcutConflict> FindConflicts()
    {
        var owners = new Dictionary<KeyChord, List<string>>();
        foreach (ShortcutAction action in _actions)
        {
            foreach (KeyChord chord in GetKeys(action.Id).Distinct())
            {
                if (!owners.TryGetValue(chord, out List<string>? list))
                {
                    owners[chord] = list = [];
                }

                list.Add(action.Id);
            }
        }

        return [.. owners.Where(o => o.Value.Count > 1).Select(o => new ShortcutConflict(o.Key, o.Value))];
    }

    /// <summary>
    /// Asigna <paramref name="chord"/> a la acción, en lugar de <paramref name="replacing"/> si se da (y la acción la
    /// tiene) o además de las que ya tiene. Si otra acción usaba esa combinación, la pierde y, con
    /// <paramref name="swap"/>, recibe a cambio <paramref name="replacing"/>: las dos intercambian sus teclas.
    /// </summary>
    public RebindResult Rebind(string actionId, KeyChord chord, KeyChord? replacing = null, bool swap = true)
    {
        if (!_byId.ContainsKey(actionId))
        {
            throw new ArgumentException($"No hay ninguna acción «{actionId}».", nameof(actionId));
        }

        if (KeyChord.IsModifierKey(chord.Key))
        {
            throw new ArgumentException("Ctrl, Mayús, Alt o Windows solos no forman un atajo.", nameof(chord));
        }

        List<KeyChord> mine = [.. GetKeys(actionId)];
        int slot = replacing is { } old ? mine.IndexOf(old) : -1;
        KeyChord? given = slot >= 0 ? mine[slot] : null;
        if (mine.Contains(chord))
        {
            if (slot < 0 || mine[slot] == chord)
            {
                return RebindResult.None;
            }

            // Ya la tenía en otro lugar: solo se quita la que se reemplazaba.
            mine.RemoveAt(slot);
            SetOverride(actionId, mine);
            OnChanged();
            return new RebindResult(true, null, null);
        }

        string? owner = FindOwner(chord, except: actionId);
        if (slot >= 0)
        {
            mine[slot] = chord;
        }
        else
        {
            mine.Add(chord);
        }

        SetOverride(actionId, mine);
        KeyChord? swapped = null;
        if (owner is not null)
        {
            List<KeyChord> theirs = [.. GetKeys(owner)];
            int at = theirs.IndexOf(chord);
            if (swap && given is { } back && FindOwner(back, except: actionId) is null && !theirs.Contains(back))
            {
                theirs[at] = back;
                swapped = back;
            }
            else
            {
                theirs.RemoveAt(at);
            }

            SetOverride(owner, theirs);
        }

        OnChanged();
        return new RebindResult(true, owner, swapped);
    }

    /// <summary>Quita una tecla de la acción (la acción puede quedar sin ninguna).</summary>
    public bool RemoveKey(string actionId, KeyChord chord)
    {
        List<KeyChord> keys = [.. GetKeys(actionId)];
        if (!keys.Remove(chord))
        {
            return false;
        }

        SetOverride(actionId, keys);
        OnChanged();
        return true;
    }

    /// <summary>Vuelve a las teclas de fábrica de la acción en el estilo actual.</summary>
    public void Reset(string actionId)
    {
        if (CurrentOverrides.Remove(actionId))
        {
            OnChanged();
        }
    }

    /// <summary>Vuelve a las teclas de fábrica de todo el estilo actual.</summary>
    public void ResetAll()
    {
        if (CurrentOverrides.Count > 0)
        {
            CurrentOverrides.Clear();
            OnChanged();
        }
    }

    /// <summary>Los cambios de la persona de un estilo, por acción (para guardarlos).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<KeyChord>> GetOverrides(ShortcutPreset preset) =>
        _overrides.TryGetValue(preset, out Dictionary<string, KeyChord[]>? map)
            ? map.ToDictionary(p => p.Key, p => (IReadOnlyList<KeyChord>)p.Value, StringComparer.Ordinal)
            : new Dictionary<string, IReadOnlyList<KeyChord>>(StringComparer.Ordinal);

    /// <summary>Reemplaza los cambios de un estilo (al leerlos del archivo).</summary>
    public void SetOverrides(ShortcutPreset preset, IReadOnlyDictionary<string, IReadOnlyList<KeyChord>> overrides)
    {
        ArgumentNullException.ThrowIfNull(overrides);
        Dictionary<string, KeyChord[]> map = Overrides(preset);
        map.Clear();
        foreach ((string id, IReadOnlyList<KeyChord> keys) in overrides)
        {
            map[id] = [.. keys.Distinct()];
        }

        OnChanged();
    }

    /// <summary>
    /// Acciones cuyo texto, explicación, grupo o tecla contienen todas las palabras buscadas, sin distinguir mayúsculas
    /// ni tildes («mayus z» encuentra Ctrl+Mayús+Z). La página de atajos busca con <see cref="ShortcutSearch"/>, que
    /// además entiende una tecla sola («S») y la distribución del teclado.
    /// </summary>
    public IEnumerable<ShortcutAction> Search(string? text)
    {
        string[] words = KeyNames.Fold(text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0)
        {
            return _actions;
        }

        return _actions.Where(action =>
        {
            string haystack = KeyNames.Fold($"{action.Label} {action.Description} {action.Group} {string.Join(' ', GetKeys(action.Id))}");
            return words.All(w => haystack.Contains(w, StringComparison.Ordinal));
        });
    }

    /// <summary>La acción que tiene la combinación, sin contar <paramref name="except"/>.</summary>
    public string? FindOwner(KeyChord chord, string? except = null) =>
        _actions.FirstOrDefault(a => a.Id != except && GetKeys(a.Id).Contains(chord))?.Id;

    /// <summary>Todos los cambios de la persona (teclas y ratón, de todos los estilos), para deshacer.</summary>
    public ShortcutSnapshot TakeSnapshot()
    {
        var keys = new Dictionary<ShortcutPreset, IReadOnlyDictionary<string, IReadOnlyList<KeyChord>>>();
        var mouse = new Dictionary<ShortcutPreset, IReadOnlyDictionary<MouseContext, IReadOnlyDictionary<MouseChord, string?>>>();
        foreach (ShortcutPreset preset in Enum.GetValues<ShortcutPreset>())
        {
            keys[preset] = GetOverrides(preset);
            mouse[preset] = Mouse.GetOverrides(preset);
        }

        return new ShortcutSnapshot(keys, mouse);
    }

    /// <summary>Deja los cambios como estaban en <paramref name="snapshot"/>: los de todos los estilos o solo los de <paramref name="only"/>.</summary>
    public void Restore(ShortcutSnapshot snapshot, ShortcutPreset? only = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        foreach (ShortcutPreset preset in Enum.GetValues<ShortcutPreset>().Where(p => only is null || p == only))
        {
            Dictionary<string, KeyChord[]> map = Overrides(preset);
            map.Clear();
            if (snapshot.Keys.TryGetValue(preset, out IReadOnlyDictionary<string, IReadOnlyList<KeyChord>>? keys))
            {
                foreach ((string id, IReadOnlyList<KeyChord> chords) in keys)
                {
                    map[id] = [.. chords.Distinct()];
                }
            }

            Mouse.SetOverrides(preset, snapshot.Mouse.GetValueOrDefault(preset) ?? new Dictionary<MouseContext, IReadOnlyDictionary<MouseChord, string?>>(), notify: false);
        }

        OnChanged();
    }

    internal void NotifyChanged() => OnChanged();

    private Dictionary<string, KeyChord[]> CurrentOverrides => Overrides(_preset);

    private Dictionary<string, KeyChord[]> Overrides(ShortcutPreset preset)
    {
        if (!_overrides.TryGetValue(preset, out Dictionary<string, KeyChord[]>? map))
        {
            _overrides[preset] = map = new Dictionary<string, KeyChord[]>(StringComparer.Ordinal);
        }

        return map;
    }

    private void SetOverride(string actionId, List<KeyChord> keys)
    {
        if (_byId.TryGetValue(actionId, out ShortcutAction? action) && action.DefaultKeys(_preset).SequenceEqual(keys))
        {
            CurrentOverrides.Remove(actionId);
        }
        else
        {
            CurrentOverrides[actionId] = [.. keys];
        }
    }

    private Dictionary<KeyChord, string> BuildLookup()
    {
        var lookup = new Dictionary<KeyChord, string>();
        foreach (ShortcutAction action in _actions)
        {
            foreach (KeyChord chord in GetKeys(action.Id))
            {
                lookup.TryAdd(chord, action.Id);
            }
        }

        return lookup;
    }

    private void OnChanged()
    {
        _lookup = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>Todos los cambios de atajos en un momento: teclas y ratón de cada estilo.</summary>
public sealed record ShortcutSnapshot(
    IReadOnlyDictionary<ShortcutPreset, IReadOnlyDictionary<string, IReadOnlyList<KeyChord>>> Keys,
    IReadOnlyDictionary<ShortcutPreset, IReadOnlyDictionary<MouseContext, IReadOnlyDictionary<MouseChord, string?>>> Mouse)
{
    public bool IsEmpty => Keys.Values.All(k => k.Count == 0) && Mouse.Values.All(m => m.Count == 0);
}
