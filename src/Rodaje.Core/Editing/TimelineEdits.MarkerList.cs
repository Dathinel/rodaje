using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Editing;

public static partial class TimelineEdits
{
    /// <summary>Cambia el nombre del marcador; un nombre vacío o solo con espacios no se admite.</summary>
    public static void RenameMarker(EditContext ctx, string markerId, string name)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(name);
        Marker marker = ResolveMarker(ctx, markerId);
        string trimmed = name.Trim();
        if (trimmed.Length == 0)
        {
            throw new EditException("El marcador necesita un nombre.");
        }

        marker.Name = trimmed;
        ctx.EnsureValid();
    }

    /// <summary>Primer marcador estrictamente después de <paramref name="at"/>; <c>null</c> si no hay.</summary>
    public static Marker? NextMarker(Sequence sequence, Time at)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        return sequence.Markers.Where(m => m.Time > at).MinBy(m => m.Time);
    }

    /// <summary>Último marcador estrictamente antes de <paramref name="at"/>; <c>null</c> si no hay.</summary>
    public static Marker? PreviousMarker(Sequence sequence, Time at)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        return sequence.Markers.Where(m => m.Time < at).MaxBy(m => m.Time);
    }
}
