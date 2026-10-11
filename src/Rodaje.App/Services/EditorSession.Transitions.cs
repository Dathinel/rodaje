using Rodaje.Core.Commands;
using Rodaje.Core.Model;

namespace Rodaje.App.Services;

/// <summary>
/// La transición seleccionada. Va aparte de los clips: seleccionar una transición vacía la selección de clips y al revés,
/// y Supr borra lo que esté seleccionado.
/// </summary>
public sealed partial class EditorSession
{
    private string? _selectedTransition;

    /// <summary>La transición seleccionada en la línea de tiempo, o <c>null</c>.</summary>
    public string? SelectedTransitionId => _selectedTransition;

    /// <summary>La transición seleccionada con su pista, si sigue en la secuencia.</summary>
    public (Track Track, TransitionInstance Transition)? SelectedTransition =>
        _selectedTransition is { } id ? FindTransition(id) : null;

    public void SelectTransition(string? transitionId)
    {
        VerifyAccess();
        if (transitionId == _selectedTransition && (transitionId is null || _selection.Count == 0))
        {
            return;
        }

        _selectedTransition = transitionId;
        if (_selection.Count > 0)
        {
            _selection = [];
            OnPropertyChanged(nameof(SelectedClipIds));
        }

        OnPropertyChanged(nameof(SelectedTransitionId));
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public (Track Track, TransitionInstance Transition)? FindTransition(string transitionId)
    {
        foreach (Track track in Sequence.AllTracks)
        {
            if (track.FindTransition(transitionId) is { } transition)
            {
                return (track, transition);
            }
        }

        return null;
    }

    private bool DeleteSelectedTransition()
    {
        if (_selectedTransition is not { } id || FindTransition(id) is null)
        {
            return false;
        }

        bool done = Execute(Edits.RemoveTransition(SequenceId, id, _ids));
        if (done)
        {
            SelectTransition(null);
        }

        return done;
    }

    /// <summary>Quita la transición seleccionada si ya no existe (deshacer, o un clip borrado); devuelve si cambió.</summary>
    private bool PruneTransition()
    {
        if (_selectedTransition is { } id && FindTransition(id) is null)
        {
            _selectedTransition = null;
            OnPropertyChanged(nameof(SelectedTransitionId));
            return true;
        }

        return false;
    }
}
