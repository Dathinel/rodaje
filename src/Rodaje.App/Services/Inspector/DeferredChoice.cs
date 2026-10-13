using Rodaje.App.Services.Audio;
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

    /// <summary>
    /// Máscaras, al despachar la elección <paramref name="choice"/> del desplegable de destino: la edición que se aplica (el
    /// destino y el uso que se guardan en la máscara) o nulo si la elección ya no vale (ver <see cref="MaskStillApplies"/>) y
    /// el panel solo se refresca para volver a mostrar el destino que tiene.
    /// </summary>
    public static MaskTargetEdit? MaskTarget(DeferredChoice<string> picked, string choice, string? maskId, string? clipId, Clip? clip)
    {
        ArgumentNullException.ThrowIfNull(choice);
        if (!MaskStillApplies(picked, maskId, clipId, clip))
        {
            return null;
        }

        bool zone = choice == MaskTargetChoices.EffectsZone;
        return new MaskTargetEdit(zone ? "" : choice, zone ? "effects" : "");
    }

    /// <summary>
    /// Dinámica: la elección (un preajuste del compresor) vale si el panel sigue con el mismo dueño y el mismo clip, y el
    /// editor del compresor sigue apuntando a ese dueño (<paramref name="editorOwner"/>; nulo si no hay editor). Si no, el
    /// panel se refresca en vez de aplicarla a otro.
    /// </summary>
    public static bool DynamicsStillApplies(DeferredChoice<DynamicsOwner> picked, DynamicsOwner? chosen, string? clipId, DynamicsOwner? editorOwner) =>
        picked.StillApplies(chosen, clipId) && editorOwner is not null && editorOwner == picked.Owner;

    /// <summary>
    /// Mezclador, al despachar la salida elegida (<paramref name="busId"/>; nulo, la mezcla): se aplica si es la mezcla o un
    /// bus que sigue estando; si el bus se quitó entre el evento y el despacho, se refresca la tira para que el desplegable
    /// no siga mostrando la elección que no se aplicó.
    /// </summary>
    public static DeferredOutcome StripOutput(IReadOnlyList<AudioBus> buses, string? busId) =>
        StripOutputChoices.StillThere(buses, busId) ? DeferredOutcome.Apply : DeferredOutcome.Refresh;
}

/// <summary>Lo que hace una vista al despachar la elección de un desplegable (ver <see cref="DeferredChoice{TOwner}"/>).</summary>
public enum DeferredOutcome
{
    /// <summary>La elección sigue valiendo: se aplica (y la edición refresca el panel).</summary>
    Apply,

    /// <summary>
    /// La elección ya no vale: no se aplica, pero se refresca para que el desplegable vuelva a mostrar lo que hay en vez de lo
    /// elegido.
    /// </summary>
    Refresh,
}

/// <summary>La edición de destino de una máscara: el efecto al que se limita (vacío, ninguno) y su uso («effects» para la zona).</summary>
public readonly record struct MaskTargetEdit(string Target, string Use);
