namespace Rodaje.App.Services.Shortcuts;

/// <summary>Qué pasó al asignar una acción a una combinación del ratón.</summary>
/// <param name="Changed">Cambió algo.</param>
/// <param name="PreviousOwner">La acción que tenía la combinación y la perdió.</param>
/// <param name="SwappedTo">Dónde quedó <paramref name="PreviousOwner"/> si se intercambiaron.</param>
public sealed record MouseRebind(bool Changed, string? PreviousOwner, MouseChord? SwappedTo)
{
    public static readonly MouseRebind None = new(false, null, null);
}

/// <summary>
/// El ratón de cada estilo y zona: una acción por combinación (botón o eje con modificadores). Son las de fábrica de
/// <see cref="MouseDefaults"/> más los cambios de la persona, que se guardan por estilo como las teclas.
/// </summary>
/// <remarks>
/// Una acción puede estar en varias combinaciones a la vez (desplazar con la rueda y con la inclinación); una
/// combinación, solo en una acción. Un cambio a <c>null</c> deja la combinación sin acción aunque de fábrica tuviera una.
/// </remarks>
public sealed class MouseMap
{
    private readonly ShortcutRegistry _registry;
    private readonly Dictionary<ShortcutPreset, Dictionary<MouseContext, Dictionary<MouseChord, string?>>> _overrides = [];

    internal MouseMap(ShortcutRegistry registry) => _registry = registry;

    private ShortcutPreset Preset => _registry.Preset;

    /// <summary>La acción de esa combinación en el estilo actual, o <c>null</c>.</summary>
    public string? Get(MouseContext context, MouseChord chord)
    {
        if (MouseGestures.Fixed(context, chord) is not null)
        {
            return null;
        }

        return Current(context).TryGetValue(chord, out string? custom) ? custom : MouseDefaults.For(Preset, context).GetValueOrDefault(chord);
    }

    /// <summary>Todas las combinaciones con acción de la zona, ordenadas por parte y modificadores.</summary>
    public IReadOnlyList<(MouseChord Chord, string ActionId)> All(MouseContext context)
    {
        var chords = new HashSet<MouseChord>(MouseDefaults.For(Preset, context).Keys);
        chords.UnionWith(Current(context).Keys);
        return [.. chords
            .Select(c => (Chord: c, ActionId: Get(context, c)))
            .Where(p => p.ActionId is not null)
            .Select(p => (p.Chord, p.ActionId!))
            .OrderBy(p => p.Chord.Input).ThenBy(p => (int)p.Chord.Modifiers)];
    }

    /// <summary>La primera combinación de la zona que lanza la acción.</summary>
    public MouseChord? FindChord(MouseContext context, string actionId, MouseChord? except = null)
    {
        foreach ((MouseChord chord, string id) in All(context))
        {
            if (id == actionId && chord != except)
            {
                return chord;
            }
        }

        return null;
    }

    public bool IsCustomized(MouseContext context, MouseChord chord) => Current(context).ContainsKey(chord);

    /// <summary>Por qué no se puede poner esa acción en esa combinación, o <c>null</c> si se puede.</summary>
    public string? Reason(MouseContext context, MouseChord chord, string actionId)
    {
        if (MouseGestures.Fixed(context, chord) is not null)
        {
            return MouseGestures.FixedReason(context, chord);
        }

        if (chord.Modifiers.HasFlag(KeyModifiers.Windows))
        {
            return "Las combinaciones con la tecla Windows son del sistema.";
        }

        if (MouseGestures.Find(actionId) is { } gesture)
        {
            if (!gesture.WorksIn(context))
            {
                return $"«{gesture.Label}» no funciona en {Where(context)}.";
            }

            return (gesture.Kind, chord.IsAxis) switch
            {
                (MouseGestureKind.Axis, false) => $"«{gesture.Label}» necesita la rueda: elige la rueda o su inclinación.",
                (MouseGestureKind.Drag, true) => $"«{gesture.Label}» se hace arrastrando: elige un botón.",
                _ => null,
            };
        }

        if (_registry.Find(actionId) is null)
        {
            return $"No hay ninguna acción «{actionId}».";
        }

        return chord.IsAxis ? "La rueda solo hace acciones de rueda (zoom, desplazar, recorrer cuadros); las demás van en los botones." : null;
    }

    /// <summary>
    /// Pone <paramref name="actionId"/> en <paramref name="chord"/>. Si la combinación era de otra acción, la pierde; con
    /// <paramref name="swap"/> esa otra pasa a <paramref name="from"/> (o a la combinación que la acción tenía en la zona).
    /// Si se da <paramref name="from"/>, la acción deja esa combinación: se mueve.
    /// </summary>
    public MouseRebind Assign(MouseContext context, MouseChord chord, string actionId, MouseChord? from = null, bool swap = false)
    {
        if (Reason(context, chord, actionId) is { } reason)
        {
            throw new ArgumentException(reason, nameof(chord));
        }

        string? owner = Get(context, chord);
        if (owner == actionId)
        {
            return MouseRebind.None;
        }

        MouseChord? source = from ?? (swap ? FindChord(context, actionId, except: chord) : null);
        Set(context, chord, actionId);
        MouseChord? swapped = null;
        if (source is { } back && back != chord)
        {
            if (swap && owner is not null && Reason(context, back, owner) is null)
            {
                Set(context, back, owner);
                swapped = back;
            }
            else if (from is not null)
            {
                Set(context, back, null);
            }
        }

        _registry.NotifyChanged();
        return new MouseRebind(true, owner, swapped);
    }

    /// <summary>Deja la combinación sin acción.</summary>
    public bool Remove(MouseContext context, MouseChord chord)
    {
        if (Get(context, chord) is null)
        {
            return false;
        }

        Set(context, chord, null);
        _registry.NotifyChanged();
        return true;
    }

    /// <summary>La parte vuelve a lo de fábrica con todos sus modificadores; con <paramref name="modifiers"/>, solo esa combinación.</summary>
    public bool Reset(MouseContext context, MouseInput input, KeyModifiers? modifiers = null)
    {
        Dictionary<MouseChord, string?> map = Current(context);
        int removed = map.Keys.Where(c => c.Input == input && (modifiers is null || c.Modifiers == modifiers)).ToList().Count(map.Remove);
        if (removed > 0)
        {
            _registry.NotifyChanged();
        }

        return removed > 0;
    }

    /// <summary>La zona vuelve a lo de fábrica del estilo actual.</summary>
    public bool ResetContext(MouseContext context)
    {
        Dictionary<MouseChord, string?> map = Current(context);
        if (map.Count == 0)
        {
            return false;
        }

        map.Clear();
        _registry.NotifyChanged();
        return true;
    }

    /// <summary>Los cambios de un estilo, por zona (para guardarlos o deshacer).</summary>
    public IReadOnlyDictionary<MouseContext, IReadOnlyDictionary<MouseChord, string?>> GetOverrides(ShortcutPreset preset)
    {
        var result = new Dictionary<MouseContext, IReadOnlyDictionary<MouseChord, string?>>();
        if (_overrides.TryGetValue(preset, out var byContext))
        {
            foreach ((MouseContext context, Dictionary<MouseChord, string?> map) in byContext)
            {
                if (map.Count > 0)
                {
                    result[context] = new Dictionary<MouseChord, string?>(map);
                }
            }
        }

        return result;
    }

    /// <summary>Reemplaza los cambios de un estilo. Las combinaciones fijas o que no valen se descartan.</summary>
    public void SetOverrides(ShortcutPreset preset, IReadOnlyDictionary<MouseContext, IReadOnlyDictionary<MouseChord, string?>> overrides, bool notify = true)
    {
        ArgumentNullException.ThrowIfNull(overrides);
        Dictionary<MouseContext, Dictionary<MouseChord, string?>> byContext = Overrides(preset);
        byContext.Clear();
        foreach ((MouseContext context, IReadOnlyDictionary<MouseChord, string?> map) in overrides)
        {
            var target = new Dictionary<MouseChord, string?>();
            foreach ((MouseChord chord, string? actionId) in map)
            {
                if (MouseGestures.Fixed(context, chord) is null && (actionId is null || Reason(context, chord, actionId) is null)
                    && MouseDefaults.For(preset, context).GetValueOrDefault(chord) != actionId)
                {
                    target[chord] = actionId;
                }
            }

            byContext[context] = target;
        }

        if (notify)
        {
            _registry.NotifyChanged();
        }
    }

    internal bool HasOverrides(ShortcutPreset preset) => GetOverrides(preset).Count > 0;

    private static string Where(MouseContext context) => context switch
    {
        MouseContext.Viewer => "el visor",
        MouseContext.Library => "la biblioteca",
        _ => "la línea de tiempo",
    };

    private void Set(MouseContext context, MouseChord chord, string? actionId)
    {
        Dictionary<MouseChord, string?> map = Current(context);
        if (MouseDefaults.For(Preset, context).GetValueOrDefault(chord) == actionId)
        {
            map.Remove(chord);
        }
        else
        {
            map[chord] = actionId;
        }
    }

    private Dictionary<MouseChord, string?> Current(MouseContext context)
    {
        Dictionary<MouseContext, Dictionary<MouseChord, string?>> byContext = Overrides(Preset);
        if (!byContext.TryGetValue(context, out Dictionary<MouseChord, string?>? map))
        {
            byContext[context] = map = [];
        }

        return map;
    }

    private Dictionary<MouseContext, Dictionary<MouseChord, string?>> Overrides(ShortcutPreset preset)
    {
        if (!_overrides.TryGetValue(preset, out var byContext))
        {
            _overrides[preset] = byContext = [];
        }

        return byContext;
    }
}
