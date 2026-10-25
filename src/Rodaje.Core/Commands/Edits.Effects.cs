using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Commands;

/// <summary>Fábricas de comandos para <see cref="EffectEdits"/>: pila de efectos, parámetros y keyframes.</summary>
/// <remarks><c>clipId</c> puede ser el de una pista: entonces se editan sus efectos (por ejemplo, el volumen de la pista).</remarks>
public static partial class Edits
{
    /// <summary>Agrega el efecto si el clip no tiene ninguno de ese tipo (por ejemplo, la transformación antes de editarla).</summary>
    /// <remarks><see cref="SequenceEditCommand.CreatedIds"/>: el efecto creado, o vacío si ya existía.</remarks>
    public static SequenceEditCommand EnsureEffect(string sequenceId, string clipId, string type, IIdGenerator? ids = null, string? description = null) =>
        new(
            description ?? "Añadir efecto",
            sequenceId,
            ctx => EffectEdits.EnsureEffect(ctx, clipId, type) is { } id ? [id] : (IReadOnlyList<string>)[],
            ids);

    /// <summary>Agrega un efecto a la pila del clip, al final o en <paramref name="index"/>.</summary>
    /// <remarks><see cref="SequenceEditCommand.CreatedIds"/>: el efecto creado.</remarks>
    public static SequenceEditCommand AddEffect(string sequenceId, string clipId, string type, int? index = null, IIdGenerator? ids = null) =>
        new("Añadir filtro", sequenceId, ctx => (IReadOnlyList<string>)[EffectEdits.AddEffect(ctx, clipId, type, index)], ids);

    /// <summary>Agrega un efecto a la pila de varios clips en un solo paso del historial.</summary>
    public static SequenceEditCommand AddEffectOnClips(string sequenceId, IEnumerable<string> clipIds, string type, IIdGenerator? ids = null)
    {
        string[] array = [.. clipIds];
        return new SequenceEditCommand(
            "Añadir filtro",
            sequenceId,
            ctx =>
            {
                List<string> created = [];
                foreach (string clipId in array)
                {
                    created.Add(EffectEdits.AddEffect(ctx, clipId, type));
                }

                return created;
            },
            ids);
    }

    /// <param name="description">Texto del historial cuando el efecto no se ve como filtro (un seguimiento, el compresor).</param>
    public static SequenceEditCommand RemoveEffect(string sequenceId, string clipId, string effectId, IIdGenerator? ids = null, string? description = null) =>
        new(description ?? "Quitar filtro", sequenceId, ctx => EffectEdits.RemoveEffect(ctx, clipId, effectId), ids);

    /// <summary>Quita un efecto de varios clips en un solo paso del historial.</summary>
    public static SequenceEditCommand RemoveEffectFromClips(string sequenceId, IEnumerable<(string ClipId, string EffectId)> targets, IIdGenerator? ids = null)
    {
        (string ClipId, string EffectId)[] array = [.. targets];
        return new SequenceEditCommand(
            "Quitar filtro",
            sequenceId,
            ctx =>
            {
                foreach ((string clipId, string effectId) in array)
                {
                    EffectEdits.RemoveEffect(ctx, clipId, effectId);
                }
            },
            ids);
    }

    public static SequenceEditCommand MoveEffect(string sequenceId, string clipId, string effectId, int newIndex, IIdGenerator? ids = null) =>
        new("Reordenar filtros", sequenceId, ctx => EffectEdits.MoveEffect(ctx, clipId, effectId, newIndex), ids);

    public static SequenceEditCommand SetEffectEnabled(string sequenceId, string clipId, string effectId, bool enabled, IIdGenerator? ids = null,
        string? description = null) =>
        new(
            description ?? (enabled ? "Activar filtro" : "Desactivar filtro"),
            sequenceId,
            ctx => EffectEdits.SetEffectEnabled(ctx, clipId, effectId, enabled),
            ids);

    /// <summary>Fija una opción del efecto (modo de mezcla, LUT, origen); un valor vacío la quita.</summary>
    public static SequenceEditCommand SetEffectOption(string sequenceId, string clipId, string effectId, string name, string value, IIdGenerator? ids = null,
        string? description = null) =>
        new(description ?? "Cambiar filtro", sequenceId, ctx => EffectEdits.SetOption(ctx, clipId, effectId, name, value), ids);

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

    /// <summary>
    /// Cambia un parámetro en varios clips a la vez (el inspector con varios seleccionados), cada uno en su instante del
    /// origen; al que no tiene el efecto se le agrega. Es un solo paso, que se funde con el siguiente del mismo parámetro
    /// en los mismos clips.
    /// </summary>
    public static SequenceEditCommand SetParameterOnClips(
        string sequenceId,
        IEnumerable<(string ClipId, Time At)> targets,
        string effectType,
        string name,
        double value,
        string? description = null,
        IIdGenerator? ids = null)
    {
        ArgumentNullException.ThrowIfNull(targets);
        return SetParameterOnClips(
            sequenceId,
            targets.Select(t => (t.ClipId, (string?)null, t.At)),
            effectType,
            name,
            value,
            description,
            ids);
    }

    /// <summary>
    /// Cambia un parámetro en varios clips a la vez, opcionalmente especificando el efecto concreto de cada clip (por ejemplo,
    /// una máscara elegida por índice).
    /// </summary>
    public static SequenceEditCommand SetParameterOnClips(
        string sequenceId,
        IEnumerable<(string ClipId, string? EffectId, Time At)> targets,
        string effectType,
        string name,
        double value,
        string? description = null,
        IIdGenerator? ids = null)
    {
        ArgumentNullException.ThrowIfNull(targets);
        (string ClipId, string? EffectId, Time At)[] points = [.. targets];
        return new(
            description ?? "Cambiar parámetro",
            sequenceId,
            ctx =>
            {
                foreach ((string clipId, string? targetEffectId, Time at) in points)
                {
                    Clip clip = ctx.Sequence.GetClip(clipId).Clip;
                    string effectId = targetEffectId
                        ?? EffectEdits.EnsureEffect(ctx, clipId, effectType)
                        ?? (clip.Effects.Find(e => e.Type == effectType && e.Enabled) ?? clip.Effects.Find(e => e.Type == effectType))!.Id;
                    EffectEdits.SetParameter(ctx, clipId, effectId, name, value, at);
                }
            },
            ids,
            $"params:{effectType}:{name}:" + string.Join(',', points.Select(p => p.ClipId + (p.EffectId is not null ? ":" + p.EffectId : ""))));
    }

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

    /// <summary>Restablece uno o varios parámetros en varios clips: valor por defecto y sin keyframes, en un solo paso.</summary>
    public static SequenceEditCommand ResetParametersOnClips(
        string sequenceId,
        IEnumerable<(string ClipId, string EffectId, IReadOnlyDictionary<string, double> Defaults)> targets,
        string? description = null,
        IIdGenerator? ids = null)
    {
        ArgumentNullException.ThrowIfNull(targets);
        (string ClipId, string EffectId, IReadOnlyDictionary<string, double> Defaults)[] points = [.. targets];
        return new(
            description ?? "Restablecer",
            sequenceId,
            ctx =>
            {
                foreach ((string clipId, string effectId, IReadOnlyDictionary<string, double> defaults) in points)
                {
                    EffectEdits.ResetParameters(ctx, clipId, effectId, defaults);
                }
            },
            ids);
    }

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

    /// <summary>Cambia la interpolación de varios keyframes en un único paso de deshacer.</summary>
    public static SequenceEditCommand SetKeyframesInterpolation(
        string sequenceId,
        string clipId,
        string effectId,
        string name,
        IReadOnlyCollection<Time> at,
        Interpolation interpolation,
        IIdGenerator? ids = null)
    {
        Time[] times = at?.ToArray() ?? throw new ArgumentNullException(nameof(at));
        return new("Cambiar interpolación", sequenceId,
            ctx => EffectEdits.SetKeyframesInterpolation(ctx, clipId, effectId, name, times, interpolation), ids);
    }

    /// <summary>Pega una selección de keyframes en un punto del mismo parámetro, en un único paso de deshacer.</summary>
    public static SequenceEditCommand PasteKeyframes(
        string sequenceId,
        string clipId,
        string effectId,
        string name,
        KeyframeClipboard clipboard,
        Time at,
        IIdGenerator? ids = null) =>
        new("Pegar keyframes", sequenceId,
            ctx => EffectEdits.PasteKeyframes(ctx, clipId, effectId, name, clipboard, at), ids);

    /// <summary>La curva Bézier del keyframe de <paramref name="at"/>; se funde con los cambios siguientes del mismo keyframe (un arrastre, un paso).</summary>
    public static SequenceEditCommand SetKeyframeEase(
        string sequenceId,
        string clipId,
        string effectId,
        string name,
        Time at,
        BezierEase? ease,
        IIdGenerator? ids = null) =>
        new(
            "Cambiar curva",
            sequenceId,
            ctx => EffectEdits.SetKeyframeEase(ctx, clipId, effectId, name, at, ease),
            ids,
            ParameterKey("ease", clipId, effectId, name, at));

    /// <summary>
    /// Las tangentes de trayectoria de la posición en el keyframe de <paramref name="at"/>, en X y en Y a la vez; se funde
    /// con los cambios siguientes del mismo keyframe.
    /// </summary>
    public static SequenceEditCommand SetPathTangents(
        string sequenceId,
        string clipId,
        string effectId,
        Time at,
        (double X, double Y)? spatialIn,
        (double X, double Y)? spatialOut,
        IIdGenerator? ids = null) =>
        new(
            "Cambiar trayectoria",
            sequenceId,
            ctx =>
            {
                EffectEdits.SetSpatialTangents(ctx, clipId, effectId, BuiltInEffects.TransformParameters.X, at, spatialIn?.X, spatialOut?.X);
                EffectEdits.SetSpatialTangents(ctx, clipId, effectId, BuiltInEffects.TransformParameters.Y, at, spatialIn?.Y, spatialOut?.Y);
            },
            ids,
            ParameterKey("trayectoria", clipId, effectId, BuiltInEffects.TransformParameters.X, at));

    /// <summary>Mueve un keyframe en el tiempo; se funde con los movimientos siguientes del mismo arrastre.</summary>
    public static SequenceEditCommand MoveKeyframe(
        string sequenceId,
        string clipId,
        string effectId,
        string name,
        Time from,
        Time to,
        string mergeKey,
        IIdGenerator? ids = null) =>
        new("Mover keyframe", sequenceId, ctx => EffectEdits.MoveKeyframe(ctx, clipId, effectId, name, from, to), ids, mergeKey);

    /// <summary>Mueve varios keyframes manteniendo sus distancias, en un único paso de deshacer.</summary>
    public static SequenceEditCommand MoveKeyframes(
        string sequenceId,
        string clipId,
        string effectId,
        string name,
        IReadOnlyCollection<Time> from,
        Time delta,
        IIdGenerator? ids = null)
    {
        Time[] times = from?.ToArray() ?? throw new ArgumentNullException(nameof(from));
        return new("Mover keyframes", sequenceId, ctx => EffectEdits.MoveKeyframes(ctx, clipId, effectId, name, times, delta), ids);
    }

    /// <summary>Sube o baja el volumen de cada clip o pista (valor y keyframes) en los dB de <paramref name="deltaDbByOwner"/>, en un solo paso.</summary>
    public static SequenceEditCommand ShiftGain(
        string sequenceId,
        IReadOnlyDictionary<string, double> deltaDbByOwner,
        string description,
        IIdGenerator? ids = null) =>
        new(description, sequenceId, ctx =>
        {
            foreach ((string owner, double delta) in deltaDbByOwner)
            {
                EffectEdits.ShiftGain(ctx, owner, delta);
            }
        }, ids);

    /// <summary>Aplica un preajuste: el efecto queda con esos valores; nulo lo quita (ver <see cref="EffectEdits.SetEffectValues"/>).</summary>
    public static SequenceEditCommand SetEffectValues(
        string sequenceId,
        string clipId,
        string type,
        IReadOnlyDictionary<string, double>? values,
        string description,
        IIdGenerator? ids = null) =>
        new(description, sequenceId, ctx => EffectEdits.SetEffectValues(ctx, clipId, type, values), ids);

    /// <summary>Fija la ganancia de toda la mezcla (<see cref="Sequence.MasterGainDb"/>).</summary>
    public static SequenceEditCommand SetMasterGain(string sequenceId, double gainDb, string description, IIdGenerator? ids = null) =>
        new(description, sequenceId, ctx =>
        {
            if (!double.IsFinite(gainDb))
            {
                throw new EditException("La ganancia maestra necesita un número.");
            }

            ctx.Sequence.MasterGainDb = Math.Round(Math.Clamp(gainDb, Sequence.MinMasterGainDb, Sequence.MaxMasterGainDb), 2);
        }, ids);

    private static string ParameterKey(string kind, string clipId, string effectId, string name, Time at) =>
        $"{kind}:{clipId}:{effectId}:{name}:{at.Ticks}";
}
