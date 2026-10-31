using Rodaje.Core.Commands;
using Rodaje.Core.Editing;

namespace Rodaje.App.Services;

/// <summary>Copiar clips, duplicarlos y pegarlos en el cursor; cada uno es un paso del historial.</summary>
public sealed partial class EditorSession
{
    /// <summary>Lo último copiado con «Copiar clips», o <c>null</c>. Sobrevive a abrir otro proyecto: al pegar se comprueba que los medios sigan.</summary>
    public ClipClipboard? Clipboard { get; private set; }

    public bool CanPaste => Clipboard is { Items.Count: > 0 };

    /// <summary>Copia la selección (con enlazados y grupos; con «ignorar grupos», solo ella). <c>false</c> si no hay nada que copiar.</summary>
    public bool CopySelection()
    {
        VerifyAccess();
        ClipClipboard? copied;
        try
        {
            copied = ClipClipboard.Capture(Sequence, _selection, Timeline.IgnoreGroups);
        }
        catch (EditException ex)
        {
            Notify(NotificationSeverity.Warning, "No se pudo copiar", ex.Message);
            return false;
        }

        if (copied is null)
        {
            return false;
        }

        Clipboard = copied;
        return true;
    }

    /// <summary>Duplica la selección a continuación de ella y deja seleccionadas las copias.</summary>
    public bool DuplicateSelection()
    {
        VerifyAccess();
        if (_selection.Count == 0)
        {
            return false;
        }

        SequenceEditCommand command = Edits.DuplicateClips(SequenceId, _selection, _ids, alone: Timeline.IgnoreGroups);
        if (!Execute(command))
        {
            return false;
        }

        Select(command.CreatedIds);
        return true;
    }

    /// <summary>Pega lo copiado con el primero en el cursor, tapando lo que haya, y deja seleccionado lo pegado.</summary>
    public bool PasteAtPlayhead()
    {
        VerifyAccess();
        if (Clipboard is not { } copied)
        {
            return false;
        }

        SequenceEditCommand command = Edits.PasteClips(SequenceId, copied, EditPosition, _ids);
        if (!Execute(command))
        {
            return false;
        }

        Select(command.CreatedIds);
        return true;
    }
}
