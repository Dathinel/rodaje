using Rodaje.Core.Model;

namespace Rodaje.App.Services.Inspector;

/// <summary>
/// A qué se dirigía la elección de un desplegable que se aplica después de su evento: el dueño que mostraba el panel (la
/// máscara, el compresor, el editor del título) y el clip.
/// </summary>
/// <remarks>
/// Editar dentro del <c>SelectionChanged</c> refresca el panel y puede rehacer el mismo desplegable, lo que cierra la app
/// desde WinUI (tarea 91); por eso la edición va después del evento. Entre la elección y el despacho puede cambiar la
/// selección o lo que muestra el panel: entonces la elección ya no vale y no se aplica a otro dueño.
/// </remarks>
/// <typeparam name="TOwner">Lo que mostraba el panel al elegir.</typeparam>
public readonly record struct DeferredChoice<TOwner>(TOwner Owner, string? ClipId)
{
    /// <summary>Si la elección sigue valiendo con el panel mostrando <paramref name="owner"/> del clip <paramref name="clipId"/>.</summary>
    public bool StillApplies(TOwner? owner, string? clipId) =>
        EqualityComparer<TOwner?>.Default.Equals(Owner, owner) && string.Equals(ClipId, clipId, StringComparison.Ordinal);
}

/// <summary>Las comprobaciones de <see cref="DeferredChoice{TOwner}"/> de cada panel.</summary>
public static class DeferredChoices
{
    /// <summary>
    /// Máscaras: la elección de destino vale si el panel sigue en la misma máscara del mismo clip y la máscara sigue en él.
    /// </summary>
    public static bool MaskStillApplies(DeferredChoice<string> picked, string? maskId, string? clipId, Clip? clip) =>
        picked.StillApplies(maskId, clipId) && clip is not null && clip.Id == picked.ClipId
        && clip.Effects.Exists(e => e.Id == picked.Owner && e.Type == BuiltInEffects.Mask);

    /// <summary>
    /// Texto: el estilo elegido vale si el panel sigue con el mismo editor y el mismo título, y el editor sigue apuntando a
    /// ese título (la selección pudo cambiar sin que el panel se refrescara todavía).
    /// </summary>
    public static bool TitleStillApplies(DeferredChoice<TitleEditor> picked, TitleEditor? editor, string? clipId) =>
        picked.ClipId is not null && picked.StillApplies(editor, clipId) && picked.Owner.Target()?.Id == picked.ClipId;
}
