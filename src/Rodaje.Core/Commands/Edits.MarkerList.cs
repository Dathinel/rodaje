using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Commands;

public static partial class Edits
{
    /// <summary>Cambia el nombre del marcador.</summary>
    public static SequenceEditCommand RenameMarker(string sequenceId, string markerId, string name, IIdGenerator? ids = null) =>
        new("Renombrar marcador", sequenceId, ctx => TimelineEdits.RenameMarker(ctx, markerId, name), ids);

    /// <summary>Cambia nombre, color y nota del marcador.</summary>
    public static SequenceEditCommand EditMarker(string sequenceId, string markerId, string name, string? color, string? note, IIdGenerator? ids = null) =>
        new("Editar marcador", sequenceId, ctx => TimelineEdits.EditMarker(ctx, markerId, name, color, note), ids);

    /// <summary>Cambia nombre, color, nota y tramo de la región.</summary>
    public static SequenceEditCommand EditRegion(string sequenceId, string regionId, string name, string? color, string? note, Time start, Time end,
        IIdGenerator? ids = null) =>
        new("Editar región", sequenceId, ctx => TimelineEdits.EditRegion(ctx, regionId, name, color, note, start, end), ids);
}
