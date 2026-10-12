using Rodaje.Core.Editing;
using Rodaje.Core.Model;

namespace Rodaje.Core.Commands;

public static partial class Edits
{
    /// <summary>Reemplaza los clips por un clip compuesto que muestra una secuencia nueva con ellos.</summary>
    /// <remarks><see cref="SequenceEditCommand.CreatedIds"/>: los compuestos (video primero) y el id de la secuencia nueva.</remarks>
    public static SequenceEditCommand CreateCompound(string sequenceId, IEnumerable<string> clipIds, string name, IIdGenerator? ids = null)
    {
        string[] selection = CopySelection(clipIds);
        return new("Crear clip compuesto", sequenceId, ctx => TimelineEdits.CreateCompound(ctx, selection, name), ids);
    }

    /// <summary>Pone en lugar del compuesto (y su enlazado) los clips que muestra.</summary>
    /// <remarks><see cref="SequenceEditCommand.CreatedIds"/>: los clips que quedan en la secuencia.</remarks>
    public static SequenceEditCommand DecomposeCompound(string sequenceId, string clipId, IIdGenerator? ids = null) =>
        new("Descomponer clip compuesto", sequenceId, ctx => TimelineEdits.DecomposeCompound(ctx, clipId), ids);
}
