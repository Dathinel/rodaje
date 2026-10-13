using Rodaje.Core.Commands;
using Rodaje.Core.Model;

namespace Rodaje.App.Services;

/// <summary>
/// Clips compuestos: crear (Alt+G) y descomponer, cada uno en un paso del historial, y entrar a editar la secuencia de un
/// compuesto (doble clic) con migas de pan para volver. Entrar no es una edición: no pasa por el historial.
/// </summary>
public sealed partial class EditorSession
{
    private readonly List<string> _sequencePath = [];

    /// <summary>Las secuencias desde la principal hasta la que se ve (la última), para las migas de pan.</summary>
    public IReadOnlyList<Sequence> SequencePath =>
        [.. _sequencePath.Select(Project.FindSequence).OfType<Sequence>(), Sequence];

    public bool CanCreateCompound => _selection.Count > 0;

    /// <summary>El compuesto seleccionado que se puede descomponer, o <c>null</c>.</summary>
    public string? SelectedCompoundId => _selection.Find(id => Sequence.FindClip(id)?.Clip.IsCompound == true);

    /// <summary>Reemplaza la selección por un clip compuesto, que queda seleccionado.</summary>
    public bool CreateCompoundFromSelection()
    {
        VerifyAccess();
        if (_selection.Count == 0)
        {
            return false;
        }

        int n = 1;
        while (Project.Sequences.Exists(s => s.Name == $"Compuesto {n}"))
        {
            n++;
        }

        SequenceEditCommand command = Edits.CreateCompound(SequenceId, _selection, $"Compuesto {n}", _ids);
        if (!Execute(command))
        {
            return false;
        }

        Select(command.CreatedIds.SkipLast(1));
        return true;
    }

    /// <summary>Pone en lugar del compuesto seleccionado (o de <paramref name="clipId"/>) los clips que muestra, seleccionados.</summary>
    public bool DecomposeCompound(string? clipId = null)
    {
        VerifyAccess();
        if ((clipId ?? SelectedCompoundId) is not { } id)
        {
            return false;
        }

        SequenceEditCommand command = Edits.DecomposeCompound(SequenceId, id, _ids);
        if (!Execute(command))
        {
            return false;
        }

        Select(command.CreatedIds);
        return true;
    }

    /// <summary>Muestra la secuencia del compuesto para editarla; las migas de pan llevan de vuelta.</summary>
    public bool EnterCompound(string clipId)
    {
        VerifyAccess();
        if (Sequence.FindClip(clipId)?.Clip.SequenceId is not { } nested || Project.FindSequence(nested) is null)
        {
            return false;
        }

        _sequencePath.Add(SequenceId);
        ShowSequence(nested);
        return true;
    }

    /// <summary>Vuelve a la secuencia <paramref name="depth"/> de <see cref="SequencePath"/> (0 es la principal).</summary>
    public bool LeaveCompound(int depth = -1)
    {
        VerifyAccess();
        if (_sequencePath.Count == 0)
        {
            return false;
        }

        depth = depth < 0 ? _sequencePath.Count - 1 : Math.Min(depth, _sequencePath.Count - 1);
        string target = _sequencePath[depth];
        _sequencePath.RemoveRange(depth, _sequencePath.Count - depth);
        ShowSequence(target);
        return true;
    }

    private void ShowSequence(string sequenceId)
    {
        if (IsSourceActive || SequenceIn is not null || SequenceOut is not null)
        {
            ResetSource(forget: false);
        }

        SequenceId = sequenceId;
        _selection = [];
        _selectedTransition = null;
        _editVersion++;
        _playback.Load(PlaybackProject(), sequenceId);
        OnPropertyChanged(nameof(SequenceId));
        OnPropertyChanged(nameof(SequencePath));
        ProjectEdited?.Invoke(this, new ProjectEditedEventArgs(EditKind.SequenceChanged, null));
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Tras deshacer la creación de un compuesto desde dentro, la secuencia que se veía, o una de la ruta, ya no existe: se
    /// sube a la última que queda antes del hueco. Si solo faltara una de la ruta, volver a ella lanzaría.
    /// </summary>
    private bool LeaveRemovedSequences()
    {
        int removed = _sequencePath.FindIndex(id => Project.FindSequence(id) is null);
        if (removed < 0 && Project.FindSequence(SequenceId) is not null)
        {
            return false;
        }

        if (removed >= 0)
        {
            _sequencePath.RemoveRange(removed, _sequencePath.Count - removed);
        }

        string target = _sequencePath.Count > 0 ? _sequencePath[^1] : Project.Sequences[0].Id;
        if (_sequencePath.Count > 0)
        {
            _sequencePath.RemoveAt(_sequencePath.Count - 1);
        }

        SequenceId = target;
        _selection = [];
        OnPropertyChanged(nameof(SequenceId));
        OnPropertyChanged(nameof(SequencePath));
        return true;
    }
}
