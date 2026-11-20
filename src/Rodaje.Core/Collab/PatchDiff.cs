namespace Rodaje.Core.Collab;

/// <summary>Saca el parche que lleva de un estado compartido a otro.</summary>
public static class PatchDiff
{
    /// <summary>
    /// El parche que convierte <paramref name="before"/> en <paramref name="after"/>, o <see langword="null"/> si son iguales.
    /// Cada elemento lleva la versión que <paramref name="versions"/> tenga para él; las listas solo llevan su orden cuando no
    /// sale de aplicar el resto. Es determinista: el mismo par de estados da el mismo parche, con los cambios ordenados por clase y
    /// por identificador.
    /// </summary>
    public static Patch? Create(SharedState before, SharedState after, ElementVersions versions, PatchHeader header)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(versions);
        ArgumentNullException.ThrowIfNull(header);

        var ops = new List<PatchOp>();
        foreach ((ElementKey key, ElementState state) in after.Elements)
        {
            if (!before.Elements.TryGetValue(key, out ElementState? previous)
                || !string.Equals(previous.Parent, state.Parent, StringComparison.Ordinal)
                || !string.Equals(previous.Content, state.Content, StringComparison.Ordinal))
            {
                ops.Add(new PatchOp(key.Kind, key.Id, state.Parent, versions.Get(key), state.Content));
            }
        }

        foreach ((ElementKey key, ElementState state) in before.Elements)
        {
            if (!after.Elements.ContainsKey(key))
            {
                ops.Add(new PatchOp(key.Kind, key.Id, state.Parent, versions.Get(key), null));
            }
        }

        ops.Sort(PatchCanon.Compare);
        var positions = new Dictionary<ElementKey, int>();
        for (int i = 0; i < ops.Count; i++)
        {
            positions.TryAdd(ops[i].Key, i);
        }

        var orders = new List<PatchOrder>();
        foreach ((string container, IReadOnlyList<string> ids) in after.Orders.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            ElementKind kind = ContainerKind(container);
            IReadOnlyList<string> previous = before.Orders.GetValueOrDefault(container) ?? [];
            var previousIds = new HashSet<string>(previous, StringComparer.Ordinal);
            var currentIds = new HashSet<string>(ids, StringComparer.Ordinal);
            IEnumerable<string> naive = previous.Where(currentIds.Contains)
                .Concat(ids.Where(id => !previousIds.Contains(id) && positions.ContainsKey(new ElementKey(kind, id)))
                    .OrderBy(id => positions[new ElementKey(kind, id)]));
            if (!naive.SequenceEqual(ids, StringComparer.Ordinal))
            {
                orders.Add(new PatchOrder(container, ids));
            }
        }

        return ops.Count == 0 && orders.Count == 0
            ? null
            : new Patch(header.Id, header.BaseOrder, header.Author, header.Description, ops, orders, []);
    }

    // Qué clase de elemento va en cada lista: los nuevos de una lista se añaden en el orden en que los aplica PatchApply.
    private static ElementKind ContainerKind(string container) => container switch
    {
        PatchContainers.Media => ElementKind.Media,
        PatchContainers.Sequences => ElementKind.Sequence,
        PatchContainers.Transcripts => ElementKind.Transcript,
        _ when container.StartsWith("vtracks:", StringComparison.Ordinal) || container.StartsWith("atracks:", StringComparison.Ordinal) => ElementKind.Track,
        _ when container.StartsWith("markers:", StringComparison.Ordinal) => ElementKind.Marker,
        _ => ElementKind.Region,
    };
}
