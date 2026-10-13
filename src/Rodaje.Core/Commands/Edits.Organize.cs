using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Commands;

public static partial class Edits
{
    /// <summary>Pone o quita (<c>null</c>) el color de etiqueta de los clips y sus enlazados.</summary>
    public static SequenceEditCommand SetClipColor(string sequenceId, IEnumerable<string> clipIds, string? color, IIdGenerator? ids = null)
    {
        string[] selection = CopySelection(clipIds);
        return new(color is null ? "Quitar color" : "Color del clip", sequenceId, ctx => TimelineEdits.SetClipColor(ctx, selection, color), ids);
    }

    /// <summary>Agrupa los clips para seleccionarlos y moverlos juntos.</summary>
    /// <remarks><see cref="SequenceEditCommand.CreatedIds"/>: el identificador del grupo.</remarks>
    public static SequenceEditCommand GroupClips(string sequenceId, IEnumerable<string> clipIds, IIdGenerator? ids = null)
    {
        string[] selection = CopySelection(clipIds);
        return new("Agrupar", sequenceId, ctx => (IReadOnlyList<string>)[TimelineEdits.GroupClips(ctx, selection)], ids);
    }

    /// <summary>Deshace los grupos de los clips.</summary>
    public static SequenceEditCommand UngroupClips(string sequenceId, IEnumerable<string> clipIds, IIdGenerator? ids = null)
    {
        string[] selection = CopySelection(clipIds);
        return new("Desagrupar", sequenceId, ctx => TimelineEdits.UngroupClips(ctx, selection), ids);
    }

    /// <summary>
    /// Mueve los clips (con enlazados y grupos) en el tiempo sin pisar nada. Los empujones seguidos de la misma selección
    /// se funden en un paso.
    /// </summary>
    public static SequenceEditCommand MoveClips(string sequenceId, IEnumerable<string> clipIds, Time delta, IIdGenerator? ids = null)
    {
        string[] selection = CopySelection(clipIds);
        return new("Mover clips", sequenceId, ctx => TimelineEdits.MoveClips(ctx, selection, delta), ids, "nudge:" + string.Join(',', selection));
    }

    /// <summary>Cambia el alto de una pista; un arrastre del borde queda como un paso.</summary>
    public static SequenceEditCommand SetTrackHeight(string sequenceId, string trackId, int? height, IIdGenerator? ids = null) =>
        new("Alto de pista", sequenceId, ctx => TimelineEdits.SetTrackHeight(ctx, trackId, height), ids, $"height:{trackId}");

    /// <summary>Cierra todos los huecos de las pistas.</summary>
    public static SequenceEditCommand CloseAllGaps(string sequenceId, IEnumerable<string> trackIds, RippleScope scope = RippleScope.Track, IIdGenerator? ids = null)
    {
        string[] tracks = CopySelection(trackIds);
        return new("Cerrar todos los huecos", sequenceId, ctx => TimelineEdits.CloseAllGaps(ctx, tracks, scope), ids);
    }

    /// <summary>Vuelve a sincronizar el clip con su enlazado del mismo medio.</summary>
    public static SequenceEditCommand Resync(string sequenceId, string clipId, ResyncMode mode, IIdGenerator? ids = null) =>
        new("Sincronizar", sequenceId, ctx => LinkSync.Resync(ctx, clipId, mode), ids);

    /// <summary>Corta al ángulo <paramref name="angle"/> de la multicámara en <paramref name="at"/>.</summary>
    /// <remarks><see cref="SequenceEditCommand.CreatedIds"/>: el clip que muestra el ángulo nuevo.</remarks>
    public static SequenceEditCommand SwitchAngle(string sequenceId, IReadOnlyList<MulticamAngle> angles, string trackId, Time at, int angle, IIdGenerator? ids = null)
    {
        ArgumentNullException.ThrowIfNull(angles);
        MulticamAngle[] copy = [.. angles];
        return new($"Ángulo {angle + 1}", sequenceId, ctx => (IReadOnlyList<string>)[MulticamEdits.SwitchAngle(ctx, copy, trackId, at, angle)], ids);
    }
}
