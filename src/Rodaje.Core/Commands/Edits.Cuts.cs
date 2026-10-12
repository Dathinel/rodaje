using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Commands;

/// <summary>Fábricas de comandos para cortar en todas las pistas y borrar a un lado del cursor.</summary>
public static partial class Edits
{
    /// <summary>Corta en <paramref name="at"/> todos los clips de las pistas desbloqueadas, en un paso del historial.</summary>
    /// <remarks><see cref="SequenceEditCommand.CreatedIds"/>: las piezas derechas nuevas, en el orden de las pistas.</remarks>
    public static SequenceEditCommand SplitAllTracks(string sequenceId, Time at, IIdGenerator? ids = null) =>
        new("Cortar en todas las pistas", sequenceId, ctx => TimelineEdits.SplitAllTracks(ctx, at), ids);

    /// <summary>
    /// Borra de los clips (y sus enlazados) lo que hay antes (<see cref="TrimEdge.In"/>) o después
    /// (<see cref="TrimEdge.Out"/>) de <paramref name="at"/>; con <paramref name="ripple"/> cierra el hueco.
    /// </summary>
    public static SequenceEditCommand DeleteToPoint(
        string sequenceId,
        IEnumerable<string> clipIds,
        Time at,
        TrimEdge side,
        bool ripple,
        RippleScope scope = RippleScope.Track,
        IIdGenerator? ids = null)
    {
        string[] selection = CopySelection(clipIds);
        return new(
            side == TrimEdge.In ? "Borrar a la izquierda del cursor" : "Borrar a la derecha del cursor",
            sequenceId,
            ctx => TimelineEdits.DeleteToPoint(ctx, selection, at, side, ripple, scope),
            ids);
    }
}
