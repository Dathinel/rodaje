namespace Rodaje.App.Services.Shortcuts;

/// <summary>Qué pasó al asignar una tecla.</summary>
/// <param name="Changed">Cambió algo.</param>
/// <param name="TakenFromActionId">La acción que tenía la tecla y la perdió, si había una.</param>
/// <param name="GivenToOther">La tecla anterior de la acción, que pasó a <paramref name="TakenFromActionId"/> (intercambio).</param>
public sealed record RebindResult(bool Changed, string? TakenFromActionId, KeyChord? GivenToOther)
{
    public static readonly RebindResult None = new(false, null, null);
}
