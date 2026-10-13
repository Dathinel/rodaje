namespace Rodaje.Core.Model;

public static partial class ModelValidator
{
    /// <summary>Anidamiento máximo de clips compuestos.</summary>
    public const int MaxCompoundDepth = 16;

    private static void ValidateCompound(Project project, Sequence sequence, Clip clip, string where, List<string> errors)
    {
        if (clip.MediaId.Length > 0 || clip.Generator is not null)
        {
            errors.Add($"{where} es compuesto y además tiene un medio o un generador.");
        }

        if (clip.Speed is not null)
        {
            errors.Add($"{where} es compuesto: no admite velocidad.");
        }

        if (project.FindSequence(clip.SequenceId!) is null)
        {
            errors.Add($"{where} usa la secuencia «{clip.SequenceId}», que no existe.");
            return;
        }

        if (CompoundDepth(project, clip.SequenceId!, [sequence.Id]) is not { } depth)
        {
            errors.Add($"{where} se contiene a sí mismo: la secuencia «{clip.SequenceId}» lleva, directa o indirectamente, a «{sequence.Id}».");
        }
        else if (depth >= MaxCompoundDepth)
        {
            errors.Add($"{where} anida más de {MaxCompoundDepth} niveles de clips compuestos.");
        }
    }

    /// <summary>
    /// Niveles de compuestos dentro de <paramref name="sequenceId"/> (0 si no tiene); <c>null</c> si hay un ciclo, es decir,
    /// si vuelve a una secuencia de <paramref name="path"/> o a una que ya la contiene. Recorre cada secuencia una sola vez
    /// (con memoria y una pila propia), así que un archivo con compuestos cruzados «en rombo» no cuesta un camino por cada
    /// combinación ni agota la pila.
    /// </summary>
    public static int? CompoundDepth(Project project, string sequenceId, IReadOnlyCollection<string> path)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(path);
        if (path.Contains(sequenceId))
        {
            return null;
        }

        var open = new HashSet<string>(path, StringComparer.Ordinal);
        var depths = new Dictionary<string, int>(StringComparer.Ordinal);
        var stack = new Stack<(string Id, IEnumerator<string> Children, int Deepest)>();

        void Enter(string id)
        {
            IEnumerable<string> children = project.FindSequence(id) is { } sequence
                ? sequence.AllTracks.SelectMany(t => t.Clips).Select(c => c.SequenceId).OfType<string>().Distinct(StringComparer.Ordinal)
                : [];
            open.Add(id);
            stack.Push((id, children.GetEnumerator(), 0));
        }

        Enter(sequenceId);
        while (stack.TryPop(out var frame))
        {
            if (frame.Children.MoveNext())
            {
                string child = frame.Children.Current;
                if (depths.TryGetValue(child, out int known))
                {
                    stack.Push(frame with { Deepest = Math.Max(frame.Deepest, known + 1) });
                }
                else if (open.Contains(child))
                {
                    return null;
                }
                else
                {
                    stack.Push(frame);
                    Enter(child);
                }

                continue;
            }

            frame.Children.Dispose();
            open.Remove(frame.Id);
            depths[frame.Id] = frame.Deepest;
            if (stack.TryPop(out var parent))
            {
                stack.Push(parent with { Deepest = Math.Max(parent.Deepest, frame.Deepest + 1) });
            }
        }

        return depths[sequenceId];
    }
}
