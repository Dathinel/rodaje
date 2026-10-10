using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Commands;

/// <summary>Fábricas de comandos para <see cref="EffectEdits"/>: pila de efectos, parámetros y keyframes.</summary>
public static partial class Edits
{
    /// <summary>Agrega el efecto si el clip no tiene ninguno de ese tipo (por ejemplo, la transformación antes de editarla).</summary>
    /// <remarks><see cref="SequenceEditCommand.CreatedIds"/>: el efecto creado, o vacío si ya existía.</remarks>
    public static SequenceEditCommand EnsureEffect(string sequenceId, string clipId, string type, IIdGenerator? ids = null) =>
        new(
            "Añadir efecto",
            sequenceId,
            ctx => EffectEdits.EnsureEffect(ctx, clipId, type) is { } id ? [id] : (IReadOnlyList<string>)[],
            ids);

    /// <summary>Agrega un efecto a la pila del clip, al final o en <paramref name="index"/>.</summary>
    /// <remarks><see cref="SequenceEditCommand.CreatedIds"/>: el efecto creado.</remarks>
    public static SequenceEditCommand AddEffect(string sequenceId, string clipId, string type, int? index = null, IIdGenerator? ids = null) =>
        new("Añadir filtro", sequenceId, ctx => (IReadOnlyList<string>)[EffectEdits.AddEffect(ctx, clipId, type, index)], ids);

    public static SequenceEditCommand RemoveEffect(string sequenceId, string clipId, string effectId, IIdGenerator? ids = null) =>
        new("Quitar filtro", sequenceId, ctx => EffectEdits.RemoveEffect(ctx, clipId, effectId), ids);

    public static SequenceEditCommand MoveEffect(string sequenceId, string clipId, string effectId, int newIndex, IIdGenerator? ids = null) =>
        new("Reordenar filtros", sequenceId, ctx => EffectEdits.MoveEffect(ctx, clipId, effectId, newIndex), ids);

    public static SequenceEditCommand SetEffectEnabled(string sequenceId, string clipId, string effectId, bool enabled, IIdGenerator? ids = null) =>
        new(
            enabled ? "Activar filtro" : "Desactivar filtro",
            sequenceId,
            ctx => EffectEdits.SetEffectEnabled(ctx, clipId, effectId, enabled),
            ids);

    /// <summary>Cambia un parámetro; si está animado, crea o fija el keyframe en <paramref name="at"/> (tiempo del origen).</summary>
    /// <remarks>
    /// <paramref name="description"/> es el texto del historial, por ejemplo «Cambiar escala». Se funde con el siguiente
    /// cambio del mismo parámetro en el mismo instante: un deslizador arrastrado deja un paso.
    /// </remarks>
    public static SequenceEditCommand SetParameter(
        string sequenceId,
        string clipId,
        string effectId,
        string name,
        double value,
        Time at,
        string? description = null,
        IIdGenerator? ids = null) =>
        new(
            description ?? "Cambiar parámetro",
            sequenceId,
            ctx => EffectEdits.SetParameter(ctx, clipId, effectId, name, value, at),
            ids,
            ParameterKey("param", clipId, effectId, name, at));

    /// <summary>Crea el keyframe en <paramref name="at"/> o cambia el que ya hay ahí.</summary>
    public static SequenceEditCommand SetKeyframe(
        string sequenceId,
        string clipId,
        string effectId,
        string name,
        Time at,
        double value,
        Interpolation? interpolation = null,
        IIdGenerator? ids = null) =>
        new(
            "Añadir keyframe",
            sequenceId,
            ctx => EffectEdits.SetKeyframe(ctx, clipId, effectId, name, at, value, interpolation),
            ids,
            ParameterKey("keyframe", clipId, effectId, name, at));

    /// <summary>Quita el keyframe de <paramref name="at"/>; sin keyframes, el parámetro queda fijo en el valor que tenía.</summary>
    public static SequenceEditCommand RemoveKeyframe(string sequenceId, string clipId, string effectId, string name, Time at, IIdGenerator? ids = null) =>
        new("Quitar keyframe", sequenceId, ctx => EffectEdits.RemoveKeyframe(ctx, clipId, effectId, name, at), ids);

    /// <summary>Restablece uno o varios parámetros del efecto: valor por defecto y sin keyframes, en un solo paso.</summary>
    public static SequenceEditCommand ResetParameters(
        string sequenceId,
        string clipId,
        string effectId,
        IReadOnlyDictionary<string, double> defaults,
        string? description = null,
        IIdGenerator? ids = null) =>
        new(
            description ?? "Restablecer",
            sequenceId,
            ctx => EffectEdits.ResetParameters(ctx, clipId, effectId, defaults),
            ids);

    public static SequenceEditCommand SetKeyframeInterpolation(
        string sequenceId,
        string clipId,
        string effectId,
        string name,
        Time at,
        Interpolation interpolation,
        IIdGenerator? ids = null) =>
        new(
            "Cambiar interpolación",
            sequenceId,
            ctx => EffectEdits.SetKeyframeInterpolation(ctx, clipId, effectId, name, at, interpolation),
            ids);

    private static string ParameterKey(string kind, string clipId, string effectId, string name, Time at) =>
        $"{kind}:{clipId}:{effectId}:{name}:{at.Ticks}";
}
