namespace Rodaje.App.Services.Shortcuts;

/// <summary>Qué pasó al asignar una acción a una combinación del ratón.</summary>
/// <param name="Changed">Cambió algo.</param>
/// <param name="PreviousOwner">La acción que tenía la combinación y la perdió.</param>
/// <param name="SwappedTo">Dónde quedó <paramref name="PreviousOwner"/> si se intercambiaron.</param>
public sealed record MouseRebind(bool Changed, string? PreviousOwner, MouseChord? SwappedTo)
{
    public static readonly MouseRebind None = new(false, null, null);
}
