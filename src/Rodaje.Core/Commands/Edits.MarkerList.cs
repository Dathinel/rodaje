using Rodaje.Core.Editing;
using Rodaje.Core.Model;

namespace Rodaje.Core.Commands;

public static partial class Edits
{
    /// <summary>Cambia el nombre del marcador.</summary>
    public static SequenceEditCommand RenameMarker(string sequenceId, string markerId, string name, IIdGenerator? ids = null) =>
        new("Renombrar marcador", sequenceId, ctx => TimelineEdits.RenameMarker(ctx, markerId, name), ids);
}
