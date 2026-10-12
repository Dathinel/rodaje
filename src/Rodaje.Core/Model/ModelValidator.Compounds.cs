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
    /// Niveles de compuestos dentro de <paramref name="sequenceId"/> (0 si no tiene); <c>null</c> si vuelve a una secuencia
    /// de <paramref name="path"/>, es decir, si hay un ciclo.
    /// </summary>
    public static int? CompoundDepth(Project project, string sequenceId, IReadOnlyCollection<string> path)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(path);
        if (path.Contains(sequenceId))
        {
            return null;
        }

        if (path.Count > MaxCompoundDepth || project.FindSequence(sequenceId) is not { } sequence)
        {
            return 0;
        }

        List<string> inner = [.. path, sequenceId];
        int deepest = 0;
        foreach (string child in sequence.AllTracks.SelectMany(t => t.Clips).Select(c => c.SequenceId).OfType<string>().Distinct())
        {
            if (CompoundDepth(project, child, inner) is not { } d)
            {
                return null;
            }

            deepest = Math.Max(deepest, d + 1);
        }

        return deepest;
    }
}
