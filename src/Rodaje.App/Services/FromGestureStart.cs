using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;

namespace Rodaje.App.Services;

/// <summary>
/// Un paso de un arrastre que se calcula desde el estado del inicio del gesto: vuelve la secuencia a <paramref name="start"/>
/// y aplica ahí <paramref name="step"/> (nada si es nulo). Encadenar pasos de un cuadro iría redondeando una y otra vez
/// (el final al cuadro, la curva a centésimas) y el resultado dependería del camino del ratón.
/// </summary>
/// <remarks>Los pasos con el mismo <paramref name="start"/> se funden: el último ya contiene todo el cambio.</remarks>
public sealed class FromGestureStart(string sequenceId, SequenceSnapshot start, ICommand? step) : ICommand
{
    private SequenceSnapshot? _before;
    private SequenceSnapshot? _after;

    public SequenceSnapshot Start { get; } = start ?? throw new ArgumentNullException(nameof(start));

    public string Description => step?.Description ?? "Volver al inicio del gesto";

    public void Apply(Project project)
    {
        Sequence sequence = FindSequence(project);
        if (_after is not null)
        {
            _after.Restore(sequence);
            return;
        }

        SequenceSnapshot before = SequenceSnapshot.Capture(sequence);
        try
        {
            Start.Restore(sequence);
            step?.Apply(project);
        }
        catch
        {
            before.Restore(sequence);
            throw;
        }

        _before = before;
        _after = SequenceSnapshot.Capture(sequence, before);
    }

    public void Revert(Project project) =>
        (_before ?? throw new InvalidOperationException("No se puede revertir un paso que no se aplicó.")).Restore(FindSequence(project));

    public bool TryMerge(ICommand following)
    {
        if (following is not FromGestureStart next || !ReferenceEquals(next.Start, Start) || _after is null || next._after is null)
        {
            return false;
        }

        _after = next._after;
        return true;
    }

    private Sequence FindSequence(Project project) =>
        project.FindSequence(sequenceId) ?? throw new EditException($"La secuencia «{sequenceId}» ya no está en el proyecto.");
}
