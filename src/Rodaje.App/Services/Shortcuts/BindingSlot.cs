namespace Rodaje.App.Services.Shortcuts;

/// <summary>
/// Una combinación de la tecla o del botón elegido en el dibujo (sola, con Ctrl, con Mayús…) y lo que hace hoy.
/// </summary>
/// <param name="Modifiers">Los modificadores de la combinación.</param>
/// <param name="Text">La combinación como se ve: «Ctrl+S», «Alt+Rueda».</param>
/// <param name="ActionId">La acción que lanza, o <c>null</c>.</param>
/// <param name="Label">El nombre de la acción, lo que hace siempre si es fija, o «Sin acción».</param>
/// <param name="Group">El grupo de la acción, para su color.</param>
/// <param name="Customized">La persona la cambió en este estilo.</param>
/// <param name="Locked">Por qué no se puede cambiar, o <c>null</c>.</param>
/// <param name="Warning">Se puede cambiar, pero conviene saber esto (AltGr).</param>
public sealed record BindingSlot(
    KeyModifiers Modifiers, string Text, string? ActionId, string Label, string? Group, bool Customized, string? Locked, string? Warning)
{
    public bool IsLocked => Locked is not null;
}
