using Rodaje.App.Views.Transcript;
using Rodaje.Core.Commands;

namespace Rodaje.App.Services;

public sealed partial class EditorSession
{
    private (TranscriptPanelModel Model, string[] Clips)? _wordSelection;

    public bool RebuildTranscript(TranscriptPanelModel model, string? guideTrackId = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        return model.Rebuild(Project, Sequence, guideTrackId, _editVersion);
    }

    public void SelectTranscriptWords(TranscriptPanelModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        Select(model.SelectedWords.Select(i => model.Document.Words[i].ClipId).Distinct());
        _wordSelection = model.SelectionCount == 0 ? null : (model, [.. _selection]);
    }

    /// <summary>
    /// Deja de tratar la selección de clips como una selección de palabras (se cerró el panel o se eligió otra cosa en la
    /// línea de tiempo): Supr vuelve a borrar clips.
    /// </summary>
    public void ForgetTranscriptSelection() => _wordSelection = null;

    public bool ExecuteTranscript(ICommand? command) => command is not null && Execute(command);

    // Los clips se seleccionan con las palabras, y un clic en el panel no siempre le da el foco: Supr llegaba entonces al
    // borrado de clips y se llevaba el clip entero (F2 del repaso del 19-nov). Si lo último elegido fueron palabras, Supr
    // borra solo esas, con el mismo ripple que en el panel.
    private bool DeleteSelectedTranscriptWords(out bool done)
    {
        done = false;
        if (_wordSelection is not { } chosen)
        {
            return false;
        }

        _wordSelection = null;
        if (chosen.Model.Version != _editVersion || !_selection.SequenceEqual(chosen.Clips, StringComparer.Ordinal)
            || chosen.Model.DeleteSelection(ripple: true) is not { } command)
        {
            return false;
        }

        done = Execute(command);
        return true;
    }
}
