using FotoToma.Core.Editing;
using FotoToma.Core.Model;
using FotoToma.Core.Timing;

namespace FotoToma.Core.Commands;

/// <summary>Fábricas de comandos para cada operación de <see cref="TimelineEdits"/>.</summary>
/// <remarks>
/// Las operaciones de arrastre llevan clave de fusión por clip y borde, y sus <c>delta</c> son relativos
/// al estado actual, así un arrastre entero se deshace en un paso.
/// </remarks>
public static class Edits
{
    /// <summary>Corta el clip (y sus enlazados que pasen por ese instante) en <paramref name="at"/>.</summary>
    /// <remarks><see cref="SequenceEditCommand.CreatedIds"/>: las piezas derechas nuevas, la del clip pedido primero.</remarks>
    public static SequenceEditCommand Split(string sequenceId, string clipId, Time at, IIdGenerator? ids = null) =>
        new("Cortar clip", sequenceId, ctx => TimelineEdits.Split(ctx, clipId, at), ids);

    /// <summary>Recorte simple de un borde, sin correr lo demás.</summary>
    public static SequenceEditCommand Trim(string sequenceId, string clipId, TrimEdge edge, Time delta, IIdGenerator? ids = null) =>
        new(
            edge == TrimEdge.In ? "Recortar inicio" : "Recortar final",
            sequenceId,
            ctx => TimelineEdits.Trim(ctx, clipId, edge, delta),
            ids,
            $"trim:{clipId}:{EdgeKey(edge)}");

    /// <summary>Recorte que corre lo que viene después para no dejar huecos ni solapes.</summary>
    public static SequenceEditCommand RippleTrim(
        string sequenceId,
        string clipId,
        TrimEdge edge,
        Time delta,
        RippleScope scope = RippleScope.Track,
        IIdGenerator? ids = null) =>
        new(
            "Recorte ripple",
            sequenceId,
            ctx => TimelineEdits.RippleTrim(ctx, clipId, edge, delta, scope),
            ids,
            $"rippletrim:{clipId}:{EdgeKey(edge)}");

    /// <summary>Mueve el corte entre el clip y el que va pegado después.</summary>
    public static SequenceEditCommand Roll(string sequenceId, string clipId, Time delta, IIdGenerator? ids = null) =>
        new("Roll", sequenceId, ctx => TimelineEdits.Roll(ctx, clipId, delta), ids, $"roll:{clipId}");

    /// <summary>Muestra otra parte del archivo sin mover el clip ni cambiar su duración.</summary>
    public static SequenceEditCommand Slip(string sequenceId, string clipId, Time delta, IIdGenerator? ids = null) =>
        new("Slip", sequenceId, ctx => TimelineEdits.Slip(ctx, clipId, delta), ids, $"slip:{clipId}");

    /// <summary>Mueve el clip entre sus vecinos pegados, que se ajustan para seguir tocándolo.</summary>
    public static SequenceEditCommand Slide(string sequenceId, string clipId, Time delta, IIdGenerator? ids = null) =>
        new("Slide", sequenceId, ctx => TimelineEdits.Slide(ctx, clipId, delta), ids, $"slide:{clipId}");

    /// <summary>Mueve el clip y su grupo enlazado, opcionalmente a otra pista del mismo tipo.</summary>
    /// <remarks>
    /// Solo <see cref="PlacementMode.Strict"/> se fusiona: en los otros modos cada paso del arrastre
    /// alteraría los clips que solo se cruzaron, así que la interfaz manda un único comando al soltar.
    /// </remarks>
    public static SequenceEditCommand Move(
        string sequenceId,
        string clipId,
        Time newStart,
        string? targetTrackId = null,
        PlacementMode mode = PlacementMode.Strict,
        IIdGenerator? ids = null) =>
        new(
            "Mover clip",
            sequenceId,
            ctx => TimelineEdits.Move(ctx, clipId, newStart, targetTrackId, mode),
            ids,
            mode == PlacementMode.Strict ? $"move:{clipId}" : null);

    /// <summary>Coloca un fragmento de un medio en <paramref name="at"/> según el modo.</summary>
    /// <remarks><see cref="SequenceEditCommand.CreatedIds"/>: los clips creados, el de <paramref name="trackId"/> primero.</remarks>
    public static SequenceEditCommand InsertMedia(
        string sequenceId,
        string mediaId,
        Time sourceIn,
        Time sourceOut,
        string trackId,
        Time at,
        PlacementMode mode = PlacementMode.Insert,
        string? linkedTrackId = null,
        IIdGenerator? ids = null) =>
        new(
            PlacementDescription(mode),
            sequenceId,
            ctx => TimelineEdits.InsertMedia(ctx, mediaId, sourceIn, sourceOut, trackId, at, mode, linkedTrackId),
            ids);

    /// <summary>Inserta un fragmento de un medio corriendo a la derecha lo que había desde <paramref name="at"/>.</summary>
    public static SequenceEditCommand Insert(
        string sequenceId,
        string mediaId,
        Time sourceIn,
        Time sourceOut,
        string trackId,
        Time at,
        string? linkedTrackId = null,
        IIdGenerator? ids = null) =>
        InsertMedia(sequenceId, mediaId, sourceIn, sourceOut, trackId, at, PlacementMode.Insert, linkedTrackId, ids);

    /// <summary>Coloca un fragmento de un medio tapando lo que había en ese tramo.</summary>
    public static SequenceEditCommand Overwrite(
        string sequenceId,
        string mediaId,
        Time sourceIn,
        Time sourceOut,
        string trackId,
        Time at,
        string? linkedTrackId = null,
        IIdGenerator? ids = null) =>
        InsertMedia(sequenceId, mediaId, sourceIn, sourceOut, trackId, at, PlacementMode.Overwrite, linkedTrackId, ids);

    /// <summary>Agrega un fragmento de un medio después del último clip de la pista.</summary>
    /// <remarks><see cref="SequenceEditCommand.CreatedIds"/>: los clips creados, el de <paramref name="trackId"/> primero.</remarks>
    public static SequenceEditCommand AppendMedia(
        string sequenceId,
        string mediaId,
        Time sourceIn,
        Time sourceOut,
        string trackId,
        string? linkedTrackId = null,
        IIdGenerator? ids = null) =>
        new(
            "Añadir al final",
            sequenceId,
            ctx => TimelineEdits.AppendMedia(ctx, mediaId, sourceIn, sourceOut, trackId, linkedTrackId),
            ids);

    /// <summary>Quita los clips (con sus enlazados) y deja el hueco.</summary>
    public static SequenceEditCommand Lift(string sequenceId, IEnumerable<string> clipIds, IIdGenerator? ids = null)
    {
        string[] selection = CopySelection(clipIds);
        return new("Levantar", sequenceId, ctx => TimelineEdits.Lift(ctx, selection), ids);
    }

    /// <summary>Quita los clips (con sus enlazados) y cierra el hueco que dejan.</summary>
    public static SequenceEditCommand RippleDelete(
        string sequenceId,
        IEnumerable<string> clipIds,
        RippleScope scope = RippleScope.Track,
        IIdGenerator? ids = null)
    {
        string[] selection = CopySelection(clipIds);
        return new("Borrado ripple", sequenceId, ctx => TimelineEdits.RippleDelete(ctx, selection, scope), ids);
    }

    /// <summary>Cierra el hueco de la pista que contiene el instante <paramref name="at"/>.</summary>
    public static SequenceEditCommand CloseGap(
        string sequenceId,
        string trackId,
        Time at,
        RippleScope scope = RippleScope.Track,
        IIdGenerator? ids = null) =>
        new("Cerrar hueco", sequenceId, ctx => TimelineEdits.CloseGap(ctx, trackId, at, scope), ids);

    /// <summary>Agrega una pista vacía en la posición dada de la lista de su tipo.</summary>
    /// <remarks><see cref="SequenceEditCommand.CreatedIds"/>: el identificador de la pista nueva.</remarks>
    public static SequenceEditCommand AddTrack(string sequenceId, TrackKind kind, int index, string name, IIdGenerator? ids = null) =>
        new("Añadir pista", sequenceId, ctx => (IReadOnlyList<string>)[TimelineEdits.AddTrack(ctx, kind, index, name)], ids);

    /// <summary>Borra la pista con todos sus clips.</summary>
    public static SequenceEditCommand RemoveTrack(string sequenceId, string trackId, IIdGenerator? ids = null) =>
        new("Quitar pista", sequenceId, ctx => TimelineEdits.RemoveTrack(ctx, trackId), ids);

    /// <summary>Cambia la posición de la pista dentro de la lista de su tipo.</summary>
    public static SequenceEditCommand MoveTrack(string sequenceId, string trackId, int newIndex, IIdGenerator? ids = null) =>
        new("Mover pista", sequenceId, ctx => TimelineEdits.MoveTrack(ctx, trackId, newIndex), ids);

    /// <summary>Cambia el silencio, el bloqueo, la visibilidad o el nombre de una pista; lo que va en <c>null</c> no cambia.</summary>
    public static SequenceEditCommand SetTrackState(
        string sequenceId,
        string trackId,
        bool? muted = null,
        bool? locked = null,
        bool? hidden = null,
        string? name = null,
        IIdGenerator? ids = null) =>
        new(
            "Cambiar pista",
            sequenceId,
            ctx => TimelineEdits.SetTrackState(ctx, trackId, muted, locked, hidden, name),
            ids);

    /// <summary>Activa o desactiva el clip y sus enlazados.</summary>
    public static SequenceEditCommand SetClipEnabled(string sequenceId, string clipId, bool enabled, IIdGenerator? ids = null) =>
        new(
            enabled ? "Activar clip" : "Desactivar clip",
            sequenceId,
            ctx => TimelineEdits.SetClipEnabled(ctx, clipId, enabled),
            ids);

    /// <summary>Enlaza los clips para que se muevan y recorten juntos.</summary>
    /// <remarks><see cref="SequenceEditCommand.CreatedIds"/>: el identificador del enlace nuevo.</remarks>
    public static SequenceEditCommand LinkClips(string sequenceId, IEnumerable<string> clipIds, IIdGenerator? ids = null)
    {
        string[] selection = CopySelection(clipIds);
        return new("Enlazar", sequenceId, ctx => (IReadOnlyList<string>)[TimelineEdits.LinkClips(ctx, selection)], ids);
    }

    /// <summary>Deshace el enlace del clip: él y sus enlazados pasan a ir sueltos.</summary>
    public static SequenceEditCommand UnlinkClips(string sequenceId, string clipId, IIdGenerator? ids = null) =>
        new("Desenlazar", sequenceId, ctx => TimelineEdits.UnlinkClips(ctx, clipId), ids);

    private static string EdgeKey(TrimEdge edge) => edge == TrimEdge.In ? "in" : "out";

    private static string PlacementDescription(PlacementMode mode) => mode switch
    {
        PlacementMode.Insert => "Insertar",
        PlacementMode.Overwrite => "Sobrescribir",
        _ => "Colocar clip",
    };

    // Copia: si la interfaz cambia su selección después, el comando no se altera.
    private static string[] CopySelection(IEnumerable<string> clipIds)
    {
        ArgumentNullException.ThrowIfNull(clipIds);
        return [.. clipIds];
    }
}
