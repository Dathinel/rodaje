using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Commands;

public static partial class Edits
{
    /// <summary>Duplica los clips (con enlazados y grupos; con <paramref name="alone"/>, solo ellos) a continuación de la selección.</summary>
    /// <remarks><see cref="SequenceEditCommand.CreatedIds"/>: los clips nuevos, en orden de tiempo.</remarks>
    public static SequenceEditCommand DuplicateClips(string sequenceId, IEnumerable<string> clipIds, IIdGenerator? ids = null, bool alone = false)
    {
        string[] selection = CopySelection(clipIds);
        return new(selection.Length == 1 ? "Duplicar clip" : "Duplicar clips", sequenceId, ctx => TimelineEdits.DuplicateClips(ctx, selection, alone), ids);
    }

    /// <summary>Pega lo copiado con el primero en <paramref name="at"/>, tapando lo que haya en ese tramo.</summary>
    /// <remarks><see cref="SequenceEditCommand.CreatedIds"/>: los clips nuevos, en el orden de lo copiado.</remarks>
    public static SequenceEditCommand PasteClips(string sequenceId, ClipClipboard copied, Time at, IIdGenerator? ids = null)
    {
        ArgumentNullException.ThrowIfNull(copied);
        return new("Pegar en el cursor", sequenceId, ctx => TimelineEdits.Paste(ctx, copied, at, PlacementMode.Overwrite), ids);
    }
}
