using System.Numerics;
using Rodaje.Core.Editing;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Commands;

public static partial class Edits
{
    /// <summary>Una máscara nueva en el clip, en un solo paso.</summary>
    /// <remarks><see cref="SequenceEditCommand.CreatedIds"/>: la máscara creada.</remarks>
    public static SequenceEditCommand AddMask(
        string sequenceId,
        string clipId,
        MaskShape shape,
        double x = 0.5,
        double y = 0.5,
        double width = 0.5,
        double height = 0.5,
        IReadOnlyList<Vector2>? path = null,
        IIdGenerator? ids = null)
    {
        Vector2[]? copy = path is null ? null : [.. path];
        return new("Añadir máscara", sequenceId,
            ctx => (IReadOnlyList<string>)[MaskEdits.AddMask(ctx, clipId, shape, x, y, width, height, copy)], ids);
    }

    /// <summary>Una Bézier o una pluma de trazo nueva, con sus vértices relativos a la caja.</summary>
    /// <remarks><see cref="SequenceEditCommand.CreatedIds"/>: la máscara creada.</remarks>
    public static SequenceEditCommand AddVertexMask(string sequenceId, string clipId, MaskShape shape, double x, double y, double width,
        double height, IEnumerable<MaskVertex> vertices, string description = "Añadir máscara", IIdGenerator? ids = null)
    {
        MaskVertex[] copy = [.. vertices];
        return new(description, sequenceId,
            ctx => (IReadOnlyList<string>)[MaskEdits.AddVertexMask(ctx, clipId, shape, x, y, width, height, copy)], ids);
    }

    /// <summary>Mueve, agrega o quita vértices (en el keyframe del cursor si la forma está animada).</summary>
    /// <remarks>Los pasos de un mismo arrastre (la misma <c>mergeKey</c>) se funden en uno.</remarks>
    public static SequenceEditCommand SetMaskVertices(string sequenceId, string clipId, string maskId, IEnumerable<MaskVertex> vertices, Time at,
        string description = "Editar la máscara", string? mergeKey = null, IIdGenerator? ids = null)
    {
        MaskVertex[] copy = [.. vertices];
        return new(description, sequenceId, ctx => MaskEdits.SetVertices(ctx, clipId, maskId, copy, at), ids, mergeKey);
    }

    public static SequenceEditCommand SetMaskShapeKey(string sequenceId, string clipId, string maskId, Time at, IIdGenerator? ids = null) =>
        new("Keyframe de la forma", sequenceId, ctx => MaskEdits.SetShapeKey(ctx, clipId, maskId, at), ids);

    public static SequenceEditCommand RemoveMaskShapeKey(string sequenceId, string clipId, string maskId, Time at, IIdGenerator? ids = null) =>
        new("Quitar keyframe de la forma", sequenceId, ctx => MaskEdits.RemoveShapeKey(ctx, clipId, maskId, at), ids);

    public static SequenceEditCommand ClearMaskShapeKeys(string sequenceId, string clipId, string maskId, Time at, IIdGenerator? ids = null) =>
        new("Quitar la animación de la forma", sequenceId, ctx => MaskEdits.ClearShapeKeys(ctx, clipId, maskId, at), ids);

    public static SequenceEditCommand ConvertMaskToBezier(string sequenceId, string clipId, string maskId, Time at, double imageAspect,
        IIdGenerator? ids = null) =>
        new("Convertir en Bézier", sequenceId, ctx => MaskEdits.ConvertToBezier(ctx, clipId, maskId, at, imageAspect), ids);

    /// <summary>La máscara limita un efecto de la pila (o la capa, con <paramref name="targetId"/> vacío).</summary>
    public static SequenceEditCommand SetMaskTarget(string sequenceId, string clipId, string maskId, string targetId, IIdGenerator? ids = null) =>
        new("Cambiar a qué se aplica la máscara", sequenceId, ctx => MaskEdits.SetTarget(ctx, clipId, maskId, targetId), ids);

    /// <remarks><see cref="SequenceEditCommand.CreatedIds"/>: la copia.</remarks>
    public static SequenceEditCommand DuplicateMask(string sequenceId, string clipId, string maskId, IIdGenerator? ids = null) =>
        new("Duplicar la máscara", sequenceId, ctx => (IReadOnlyList<string>)[MaskEdits.Duplicate(ctx, clipId, maskId)], ids);

    /// <summary>«Desenfocar zona» o «Pixelar zona»: el efecto y su máscara en un solo paso.</summary>
    /// <remarks><see cref="SequenceEditCommand.CreatedIds"/>: el efecto y la máscara.</remarks>
    public static SequenceEditCommand AddMaskedZone(string sequenceId, string clipId, ZoneEffect kind, IIdGenerator? ids = null) =>
        new(kind == ZoneEffect.Blur ? "Desenfocar zona" : "Pixelar zona", sequenceId, ctx =>
        {
            (string effectId, string maskId) = MaskEdits.AddZone(ctx, clipId, kind);
            return (IReadOnlyList<string>)[effectId, maskId];
        }, ids);

    /// <summary>El resultado del seguimiento, en un solo paso (los pasos con la misma <paramref name="mergeKey"/> se funden).</summary>
    public static SequenceEditCommand ApplyMaskTrack(string sequenceId, string clipId, string maskId, IEnumerable<MaskTrackKey> keys,
        MaskTrackParts parts, string? mergeKey = null, IIdGenerator? ids = null)
    {
        MaskTrackKey[] copy = [.. keys];
        return new("Seguir la máscara", sequenceId, ctx => MaskEdits.ApplyTrack(ctx, clipId, maskId, copy, parts), ids, mergeKey);
    }

    /// <summary>«Quitar fondo»: la máscara de imagen del clip con su video gris, en un solo paso.</summary>
    public static SequenceEditCommand SetImageMask(string sequenceId, string clipId, string matte, Time matteStart, IIdGenerator? ids = null) =>
        new("Quitar fondo", sequenceId, ctx => (IReadOnlyList<string>)[MaskEdits.SetImageMask(ctx, clipId, matte, matteStart)], ids);

    /// <summary>«Reencuadre automático»: posición y escala con keyframes, en un solo paso.</summary>
    public static SequenceEditCommand SetTransformKeys(string sequenceId, string clipId, IEnumerable<TransformKey> keys, IIdGenerator? ids = null)
    {
        TransformKey[] copy = [.. keys];
        return new("Reencuadre automático", sequenceId, ctx => MaskEdits.SetTransformKeys(ctx, clipId, copy), ids);
    }
}
