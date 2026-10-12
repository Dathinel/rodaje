namespace Rodaje.App.Services.Shortcuts;

/// <summary>
/// Una combinación de la tecla o del botón elegido en el dibujo (sola, con Ctrl, con Mayús…) y lo que hace hoy.
/// </summary>
/// <param name="Modifiers">Los modificadores de la combinación.</param>
/// <param name="Text">La combinación como se ve: «Ctrl+S», «Alt+Rueda».</param>
/// <param name="ActionId">La acción que lanza, o <c>null</c>.</param>
/// <param name="Label">El nombre de la acción, lo que hace siempre si es fija, o «Sin acción».</param>
/// <param name="Group">El grupo de la acción, para su color.</param>
/// <param name="Customized">La persona la cambió en este estilo.</param>
/// <param name="Locked">Por qué no se puede cambiar, o <c>null</c>.</param>
/// <param name="Warning">Se puede cambiar, pero conviene saber esto (AltGr).</param>
public sealed record BindingSlot(
    KeyModifiers Modifiers, string Text, string? ActionId, string Label, string? Group, bool Customized, string? Locked, string? Warning)
{
    public bool IsLocked => Locked is not null;
}

/// <summary>Una acción que se puede elegir para una combinación, en la lista con búsqueda.</summary>
public sealed record BindingChoice(string Id, string Label, string Group, string Description);

/// <summary>Lo que la página muestra al elegir una tecla o un botón en el dibujo, y qué acciones se pueden poner.</summary>
public static class BindingSlots
{
    public const string NoAction = "Sin acción";

    /// <summary>Las ocho combinaciones de una tecla física con Ctrl, Mayús y Alt.</summary>
    public static IReadOnlyList<BindingSlot> ForKey(ShortcutRegistry registry, int virtualKey, Func<KeyChord, string>? keyText = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        keyText ??= KeyNames.Format;
        var slots = new List<BindingSlot>();
        foreach (KeyModifiers modifiers in MouseChord.ModifierLayers)
        {
            var chord = new KeyChord(virtualKey, modifiers);
            string? owner = registry.FindOwner(chord);
            string? factory = registry.Actions.FirstOrDefault(a => a.DefaultKeys(registry.Preset).Contains(chord))?.Id;
            ShortcutAction? action = owner is null ? null : registry.Find(owner);
            string? locked = ReservedKeys.Reason(chord);
            slots.Add(new BindingSlot(
                modifiers,
                keyText(chord),
                owner,
                action?.Label ?? (locked is null ? NoAction : "Reservada"),
                action?.Group,
                owner != factory,
                locked,
                ReservedKeys.Warning(chord)));
        }

        return slots;
    }

    /// <summary>Las ocho combinaciones de una parte del ratón en una zona.</summary>
    public static IReadOnlyList<BindingSlot> ForMouse(ShortcutRegistry registry, MouseContext context, MouseInput input)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var slots = new List<BindingSlot>();
        foreach (KeyModifiers modifiers in MouseChord.ModifierLayers)
        {
            var chord = new MouseChord(input, modifiers);
            if (MouseGestures.Fixed(context, chord) is { } fixedLabel)
            {
                slots.Add(new BindingSlot(modifiers, chord.ToString(), null, fixedLabel, MouseGestures.Group, false, MouseGestures.FixedReason(context, chord), null));
                continue;
            }

            string? id = registry.Mouse.Get(context, chord);
            slots.Add(new BindingSlot(
                modifiers,
                chord.ToString(),
                id,
                id is null ? NoAction : LabelOf(registry, id),
                id is null ? null : GroupOf(registry, id),
                registry.Mouse.IsCustomized(context, chord),
                null,
                null));
        }

        return slots;
    }

    /// <summary>Las acciones del teclado que coinciden con la búsqueda (sin tildes ni mayúsculas).</summary>
    public static IReadOnlyList<BindingChoice> KeyChoices(ShortcutRegistry registry, string? query)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return Filter(registry.Actions.Select(a => new BindingChoice(a.Id, a.Label, a.Group, a.Description)), query);
    }

    /// <summary>
    /// Lo que se puede poner en esa parte del ratón: en la rueda, sus acciones de rueda; en un botón, arrastrar (si la
    /// zona lo permite) y cualquier acción del teclado.
    /// </summary>
    public static IReadOnlyList<BindingChoice> MouseChoices(ShortcutRegistry registry, MouseContext context, MouseChord chord, string? query)
    {
        ArgumentNullException.ThrowIfNull(registry);
        IEnumerable<BindingChoice> gestures = MouseGestures.All
            .Where(g => registry.Mouse.Reason(context, chord, g.Id) is null)
            .Select(g => new BindingChoice(g.Id, g.Label, MouseGestures.Group, g.Description));
        IEnumerable<BindingChoice> actions = chord.IsAxis
            ? []
            : registry.Actions.Select(a => new BindingChoice(a.Id, a.Label, a.Group, a.Description));
        return Filter(gestures.Concat(actions), query);
    }

    public static string LabelOf(ShortcutRegistry registry, string actionId) =>
        MouseGestures.Find(actionId)?.Label ?? registry.Find(actionId)?.Label ?? actionId;

    public static string ShortLabelOf(ShortcutRegistry registry, string actionId) =>
        MouseGestures.Find(actionId)?.ShortLabel ?? registry.Find(actionId)?.ShortLabel ?? actionId;

    public static string GroupOf(ShortcutRegistry registry, string actionId) =>
        MouseGestures.IsGesture(actionId) ? MouseGestures.Group : registry.Find(actionId)?.Group ?? MouseGestures.Group;

    private static List<BindingChoice> Filter(IEnumerable<BindingChoice> choices, string? query)
    {
        string[] words = KeyNames.Fold(query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return [.. choices.Where(c =>
        {
            string haystack = KeyNames.Fold($"{c.Label} {c.Group} {c.Description}");
            return words.All(w => haystack.Contains(w, StringComparison.Ordinal));
        })];
    }
}
