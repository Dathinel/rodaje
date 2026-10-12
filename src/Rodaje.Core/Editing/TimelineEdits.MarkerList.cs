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

    /// <summary>
    /// Cambia nombre, color y nota del marcador. El nombre y la nota se recortan (una nota vacía se quita); el color es
    /// <c>#RRGGBB</c> o nulo para el de siempre.
    /// </summary>
    public static void EditMarker(EditContext ctx, string markerId, string name, string? color, string? note)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(name);
        Marker marker = ResolveMarker(ctx, markerId);
        (marker.Name, marker.Color, marker.Note) = Details(name, color, note);
        ctx.EnsureValid();
    }

    /// <summary>Cambia nombre, color, nota y tramo de la región, con las mismas reglas que <see cref="EditMarker"/>.</summary>
    public static void EditRegion(EditContext ctx, string regionId, string name, string? color, string? note, Time start, Time end)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(name);
        Region region = ctx.Sequence.Regions.Find(r => r.Id == regionId)
            ?? throw new EditException($"No existe la región «{regionId}» en la secuencia.");
        EnsureNotNegative(start, "La región");
        if (end <= start)
        {
            throw new EditException("La región tiene que terminar después de empezar.");
        }

        (region.Name, region.Color, region.Note) = Details(name, color, note);
        (region.Start, region.End) = (start, end);
        EditContext.SortMarkers(ctx.Sequence);
        ctx.EnsureValid();
    }

    private static (string Name, string? Color, string? Note) Details(string name, string? color, string? note)
    {
        if (color is not null && !MarkerStyle.IsColor(color))
        {
            throw new EditException($"«{color}» no es un color #RRGGBB.");
        }

        string? trimmedNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (trimmedNote is { Length: > MarkerStyle.MaxNoteLength })
        {
            throw new EditException($"La nota no puede pasar de {MarkerStyle.MaxNoteLength} caracteres.");
        }

        return (name.Trim(), color?.ToUpperInvariant(), trimmedNote);
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
