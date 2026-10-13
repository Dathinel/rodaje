using Rodaje.Core.Editing;
using Rodaje.Core.Model;

namespace Rodaje.Core.Commands;

public static partial class Edits
{
    /// <summary>Edición de tres o cuatro puntos: inserta o sobrescribe el tramo marcado del origen en las pistas destino.</summary>
    /// <remarks><see cref="SequenceEditCommand.CreatedIds"/>: los clips creados, el de video primero.</remarks>
    public static SequenceEditCommand ThreePointEdit(string sequenceId, ThreePointRequest request, IIdGenerator? ids = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new(request.Mode == PlacementMode.Insert ? "Insertar" : "Sobrescribir", sequenceId, ctx => TimelineEdits.ThreePointEdit(ctx, request), ids);
    }

    /// <summary>Una de las cinco ediciones desde el origen: insertar, sobrescribir, reemplazar, ajustar a rellenar o superponer.</summary>
    /// <remarks><see cref="SequenceEditCommand.CreatedIds"/>: los clips creados, el de video primero.</remarks>
    public static SequenceEditCommand ThreePointEdit(string sequenceId, ThreePointRequest request, ThreePointAction action, IIdGenerator? ids = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        string name = action switch
        {
            ThreePointAction.Insert => "Insertar",
            ThreePointAction.Overwrite => "Sobrescribir",
            ThreePointAction.Replace => "Reemplazar",
            ThreePointAction.FitToFill => "Ajustar a rellenar",
            _ => "Superponer",
        };
        return new(name, sequenceId, ctx => TimelineEdits.ThreePointEdit(ctx, request, action), ids);
    }
}
