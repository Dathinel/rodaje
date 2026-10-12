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
