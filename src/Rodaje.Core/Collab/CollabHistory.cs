namespace Rodaje.Core.Collab;

/// <summary>Por qué no se puede deshacer o rehacer un paso en sesión.</summary>
public enum HistoryFailure
{
    /// <summary>No hay pasos.</summary>
    Nothing,

    /// <summary>Alguien cambió después alguno de los elementos del paso: «No se puede deshacer: Ana cambió ese clip».</summary>
    ChangedByOthers,
}

/// <summary>Un paso listo para mandar: el parche y su descripción.</summary>
public sealed record HistoryPlan(Patch Patch, string Description);

/// <summary>El resultado de preparar un deshacer o un rehacer: un plan o la razón de que no se pueda.</summary>
/// <param name="Plan">El parche listo, o <see langword="null"/>.</param>
/// <param name="Failure">Por qué no se puede, o <see langword="null"/>.</param>
/// <param name="Element">En <see cref="HistoryFailure.ChangedByOthers"/>, el primer elemento que ya no está como lo dejó el paso.</param>
public sealed record HistoryOutcome(HistoryPlan? Plan, HistoryFailure? Failure, ElementKey? Element = null);

/// <summary>
/// Deshacer y rehacer de cada persona en una sala, con parches inversos en lugar de <c>UndoStack</c> (que restauraría la
/// secuencia entera y borraría lo que llegó de otros). Un paso solo se deshace si el contenido de sus elementos sigue siendo el
/// que dejó el paso; deshacer y rehacer son parches nuevos que se mandan como cualquier otro. Sin sesión, nada de esto se usa.
/// </summary>
public sealed class CollabHistory
{
    private readonly List<Entry> _undo = [];
    private readonly List<Entry> _redo = [];

    public CollabHistory(int limit = 500)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        Limit = limit;
    }

    public int Limit { get; }

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    public string? UndoDescription => _undo.Count > 0 ? _undo[^1].Description : null;

    public string? RedoDescription => _redo.Count > 0 ? _redo[^1].Description : null;

    /// <summary>Anota un paso propio ya confirmado: el estado antes y después. Lo que se pudiera rehacer se pierde.</summary>
    public void Record(SharedState before, SharedState after, string description)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(description);
        var elements = new Dictionary<ElementKey, (ElementState? Before, ElementState? After)>();
        foreach (ElementKey key in before.Elements.Keys.Union(after.Elements.Keys))
        {
            before.Elements.TryGetValue(key, out ElementState? was);
            after.Elements.TryGetValue(key, out ElementState? now);
            if (!Same(was, now))
            {
                elements[key] = (was, now);
            }
        }

        var orders = new Dictionary<string, (IReadOnlyList<string>? Before, IReadOnlyList<string>? After)>(StringComparer.Ordinal);
        foreach (string container in before.Orders.Keys.Union(after.Orders.Keys))
        {
            before.Orders.TryGetValue(container, out IReadOnlyList<string>? was);
            after.Orders.TryGetValue(container, out IReadOnlyList<string>? now);
            if (!(was ?? []).SequenceEqual(now ?? [], StringComparer.Ordinal))
            {
                orders[container] = (was, now);
            }
        }

        if (elements.Count == 0 && orders.Count == 0)
        {
            return;
        }

        _redo.Clear();
        _undo.Add(new Entry(description, elements, orders));
        if (_undo.Count > Limit)
        {
            _undo.RemoveAt(0);
        }
    }

    /// <summary>Prepara el parche que deshace el último paso sobre el estado actual, sin tocar la pila.</summary>
    public HistoryOutcome PlanUndo(SharedState current, ElementVersions versions, PatchHeader header) =>
        Plan(_undo, current, versions, header, undo: true);

    /// <summary>Prepara el parche que rehace el último paso deshecho, sin tocar la pila.</summary>
    public HistoryOutcome PlanRedo(SharedState current, ElementVersions versions, PatchHeader header) =>
        Plan(_redo, current, versions, header, undo: false);

    /// <summary>El anfitrión aceptó el parche de <see cref="PlanUndo"/>: el paso pasa a poder rehacerse.</summary>
    public void CommitUndo()
    {
        if (_undo.Count > 0)
        {
            _redo.Add(_undo[^1]);
            _undo.RemoveAt(_undo.Count - 1);
        }
    }

    /// <summary>El anfitrión aceptó el parche de <see cref="PlanRedo"/>: el paso vuelve a poder deshacerse.</summary>
    public void CommitRedo()
    {
        if (_redo.Count > 0)
        {
            _undo.Add(_redo[^1]);
            _redo.RemoveAt(_redo.Count - 1);
        }
    }

    /// <summary>Descarta el paso que no se pudo deshacer (alguien tocó su contenido), como Figma.</summary>
    public void DiscardUndo()
    {
        if (_undo.Count > 0)
        {
            _undo.RemoveAt(_undo.Count - 1);
        }
    }

    /// <summary>Descarta todo (al salir de la sala o al reemplazar el proyecto desde fuera).</summary>
    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
    }

    private sealed record Entry(
        string Description,
        Dictionary<ElementKey, (ElementState? Before, ElementState? After)> Elements,
        Dictionary<string, (IReadOnlyList<string>? Before, IReadOnlyList<string>? After)> Orders);

    private static bool Same(ElementState? a, ElementState? b) =>
        a is null || b is null ? a is null && b is null : a.Parent == b.Parent && string.Equals(a.Content, b.Content, StringComparison.Ordinal);

    private static HistoryOutcome Plan(List<Entry> stack, SharedState current, ElementVersions versions, PatchHeader header, bool undo)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(versions);
        ArgumentNullException.ThrowIfNull(header);
        if (stack.Count == 0)
        {
            return new HistoryOutcome(null, HistoryFailure.Nothing);
        }

        Entry entry = stack[^1];
        var elements = new Dictionary<ElementKey, ElementState>(current.Elements);
        foreach ((ElementKey key, (ElementState? before, ElementState? after) states) in entry.Elements.OrderBy(p => p.Key.ToString(), StringComparer.Ordinal))
        {
            ElementState? expected = undo ? states.after : states.before;
            ElementState? wanted = undo ? states.before : states.after;
            current.Elements.TryGetValue(key, out ElementState? now);
            if (!Same(now, expected))
            {
                return new HistoryOutcome(null, HistoryFailure.ChangedByOthers, key);
            }

            if (wanted is null)
            {
                elements.Remove(key);
            }
            else
            {
                elements[key] = wanted;
            }
        }

        // Quitar una pista o una secuencia se llevaría lo que otra persona haya puesto dentro después.
        var removed = new HashSet<ElementKey>(entry.Elements.Where(p => (undo ? p.Value.Before : p.Value.After) is null).Select(p => p.Key));
        foreach (ElementKey gone in removed.Where(k => k.Kind is ElementKind.Track or ElementKind.Sequence))
        {
            foreach ((ElementKey child, ElementState state) in current.Elements)
            {
                if (state.Parent == gone.Id && !entry.Elements.ContainsKey(child))
                {
                    return new HistoryOutcome(null, HistoryFailure.ChangedByOthers, child);
                }
            }
        }

        // Si otra persona reordenó los mismos elementos, el orden del paso ya no es el de ahora.
        foreach ((string container, (IReadOnlyList<string>? before, IReadOnlyList<string>? after) lists) in entry.Orders)
        {
            IReadOnlyList<string>? expected = undo ? lists.after : lists.before;
            if (expected is null || !current.Orders.TryGetValue(container, out IReadOnlyList<string>? now))
            {
                continue;
            }

            var inExpected = new HashSet<string>(expected, StringComparer.Ordinal);
            var inNow = new HashSet<string>(now, StringComparer.Ordinal);
            string[] seen = [.. now.Where(inExpected.Contains)];
            string[] wanted = [.. expected.Where(inNow.Contains)];
            if (!seen.SequenceEqual(wanted, StringComparer.Ordinal))
            {
                return new HistoryOutcome(null, HistoryFailure.ChangedByOthers, new ElementKey(PatchDiff.ContainerKind(container), seen.Zip(wanted, (x, y) => x == y ? null : x).FirstOrDefault(x => x is not null) ?? seen[0]));
            }
        }

        var orders = new Dictionary<string, IReadOnlyList<string>>(current.Orders, StringComparer.Ordinal);
        foreach ((string container, (IReadOnlyList<string>? before, IReadOnlyList<string>? after) lists) in entry.Orders)
        {
            IReadOnlyList<string>? wanted = undo ? lists.before : lists.after;
            if (wanted is null)
            {
                orders.Remove(container);
                continue;
            }

            // Los que pide el paso, en su orden, y detrás los que añadió otra persona a esa lista.
            IReadOnlyList<string> now = current.Orders.GetValueOrDefault(container) ?? [];
            ElementKind kind = PatchDiff.ContainerKind(container);
            var present = new HashSet<string>(elements.Keys.Where(k => k.Kind == kind).Select(k => k.Id), StringComparer.Ordinal);
            var wantedSet = new HashSet<string>(wanted, StringComparer.Ordinal);
            orders[container] = [.. wanted.Where(present.Contains), .. now.Where(id => !wantedSet.Contains(id) && present.Contains(id))];
        }

        var target = new SharedState(current.ProjectId, elements, orders);
        Patch? patch = PatchDiff.Create(current, target, versions, header);
        return patch is null
            ? new HistoryOutcome(null, HistoryFailure.Nothing)
            : new HistoryOutcome(new HistoryPlan(patch, entry.Description), null);
    }
}
