using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Editing;

/// <summary>Qué se pega con «Pegar atributos».</summary>
[Flags]
public enum ClipAttributeKinds
{
    None = 0,

    /// <summary>Los filtros y efectos de la pila (color, viñeta, desenfoque, los de paquetes...).</summary>
    Effects = 1,

    /// <summary>Posición, escala, rotación, opacidad y modo de mezcla.</summary>
    Transform = 2,

    /// <summary>Velocidad constante o curva, reversa, tono y muestreo de cuadros.</summary>
    Speed = 4,

    /// <summary>Volumen y fundidos del audio.</summary>
    Volume = 8,

    All = Effects | Transform | Speed | Volume,
}

/// <summary>
/// Copia de los atributos de un clip y de sus enlazados, tomada con <see cref="Capture"/> para pegarla después en otros
/// con <see cref="ClipAttributeEdits.Paste"/>. Lo de imagen sale del clip de video del grupo (el elegido si es de video) y
/// lo de sonido, del de audio; la velocidad la comparte todo el grupo. Los keyframes se guardan como distancia al inicio
/// del clip en la línea de tiempo, así la animación cae en el mismo lugar del clip donde se pegue; lo que no cabe en el
/// destino se recorta en su borde.
/// </summary>
public sealed class ClipAttributes
{
    private ClipAttributes(string clipId, Side? video, Side? audio, ClipSpeed? speed, bool hasSpeed)
    {
        ClipId = clipId;
        Video = video;
        Audio = audio;
        Speed = speed;
        HasSpeed = hasSpeed;
    }

    /// <summary>El clip del que se copió.</summary>
    public string ClipId { get; }

    /// <summary>Lo que se puede pegar: imagen si el grupo tiene video, volumen si tiene audio y velocidad si no es un título ni un congelado.</summary>
    public ClipAttributeKinds Available =>
        (Video is not null ? ClipAttributeKinds.Effects | ClipAttributeKinds.Transform : ClipAttributeKinds.None)
        | (Audio is not null ? ClipAttributeKinds.Effects | ClipAttributeKinds.Volume : ClipAttributeKinds.None)
        | (HasSpeed ? ClipAttributeKinds.Speed : ClipAttributeKinds.None);

    internal Side? Video { get; }

    internal Side? Audio { get; }

    /// <summary>Velocidad del grupo; nula a 1×.</summary>
    internal ClipSpeed? Speed { get; }

    internal bool HasSpeed { get; }

    /// <summary>Copia los atributos del clip y de sus enlazados.</summary>
    public static ClipAttributes Capture(Sequence sequence, string clipId)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        if (clipId is null || sequence.FindClip(clipId) is not { } found)
        {
            throw new EditException($"No existe el clip «{clipId}» en la secuencia.");
        }

        List<(Track Track, Clip Clip)> group = sequence.GetLinkGroup(clipId);
        Side? video = SideOf(group, found, TrackKind.Video);
        Side? audio = SideOf(group, found, TrackKind.Audio);
        Clip clip = found.Clip;
        bool hasSpeed = clip.Generator is null && clip.Speed is not { Freeze: true };
        return new ClipAttributes(clipId, video, audio, hasSpeed ? clip.Speed?.Clone() : null, hasSpeed);
    }

    private static Side? SideOf(List<(Track Track, Clip Clip)> group, (Track Track, Clip Clip) chosen, TrackKind kind)
    {
        Clip? clip = chosen.Track.Kind == kind ? chosen.Clip : group.Find(m => m.Track.Kind == kind).Clip;
        if (clip is null)
        {
            return null;
        }

        var map = new KeyframeMap(clip);
        var effects = new List<EffectInstance>();
        foreach (EffectInstance effect in clip.Effects)
        {
            if (ClipAttributeEdits.KindOf(effect) is ClipAttributeKinds.None)
            {
                continue;
            }

            EffectInstance copy = effect.Clone();
            foreach (Parameter parameter in copy.Parameters.Values)
            {
                parameter.Keyframes = parameter.Keyframes.ConvertAll(k => k with { Time = map.ToOffset(k.Time) });
            }

            MaskEdits.MapShapeKeys(copy, map.ToOffset);
            effects.Add(copy);
        }

        return new Side(effects);
    }

    /// <summary>Los efectos de un lado, con los keyframes en distancia al inicio del clip.</summary>
    internal sealed record Side(List<EffectInstance> Effects);
}

/// <summary>Pegar atributos copiados con <see cref="ClipAttributes.Capture"/> en uno o varios clips.</summary>
public static class ClipAttributeEdits
{
    /// <summary>
    /// Pega en los clips (y sus enlazados) los atributos elegidos. Cada categoría reemplaza la del destino: sus efectos
    /// de esa categoría se quitan y entran copias de los del origen donde estaba el primero (o al final). Lo de imagen va
    /// a los clips de video y lo de sonido a los de audio; la velocidad, una vez por grupo y con el magnetismo de
    /// <paramref name="scope"/>, salvo en títulos y congelados. Lo que no aplica a un clip se salta; si no aplica a
    /// ninguno, la edición se rechaza.
    /// </summary>
    public static void Paste(
        EditContext ctx,
        IReadOnlyCollection<string> clipIds,
        ClipAttributes attributes,
        ClipAttributeKinds kinds,
        RippleScope scope = RippleScope.Track)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(clipIds);
        ArgumentNullException.ThrowIfNull(attributes);
        kinds &= attributes.Available;
        if (kinds == ClipAttributeKinds.None)
        {
            throw new EditException("No hay atributos copiados de ese tipo para pegar.");
        }

        List<(Track Track, Clip Clip)> targets = TrackOps.ExpandSelection(ctx, clipIds);
        bool changed = false;
        if ((kinds & ClipAttributeKinds.Speed) != 0)
        {
            changed |= PasteSpeed(ctx, targets, attributes, scope);
        }

        ClipAttributeKinds effectKinds = kinds & ~ClipAttributeKinds.Speed;
        if (effectKinds != ClipAttributeKinds.None)
        {
            foreach ((Track track, Clip clip) in targets)
            {
                ClipAttributes.Side? side = track.Kind == TrackKind.Video ? attributes.Video : attributes.Audio;
                ClipAttributeKinds applicable = effectKinds & (track.Kind == TrackKind.Video
                    ? ClipAttributeKinds.Effects | ClipAttributeKinds.Transform
                    : ClipAttributeKinds.Effects | ClipAttributeKinds.Volume);
                if (side is null || applicable == ClipAttributeKinds.None)
                {
                    continue;
                }

                EditContext.EnsureUnlocked(track);
                clip.Effects = Merge(ctx, clip, side, applicable);
                changed = true;
            }
        }

        if (!changed)
        {
            throw new EditException("Los clips elegidos no admiten los atributos copiados.");
        }

        ctx.EnsureValid();
    }

    /// <summary>Categoría de un efecto; <see cref="ClipAttributeKinds.None"/> para la velocidad antigua, que no se copia.</summary>
    internal static ClipAttributeKinds KindOf(EffectInstance effect) => effect.Type switch
    {
        BuiltInEffects.Transform or BuiltInEffects.Blend => ClipAttributeKinds.Transform,
        BuiltInEffects.Audio => ClipAttributeKinds.Volume,
        BuiltInEffects.Speed or BuiltInEffects.Track => ClipAttributeKinds.None,
        _ => ClipAttributeKinds.Effects,
    };

    private static List<EffectInstance> Merge(EditContext ctx, Clip clip, ClipAttributes.Side side, ClipAttributeKinds kinds)
    {
        var map = new KeyframeMap(clip);
        var pasted = new List<EffectInstance>();
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (EffectInstance source in side.Effects)
        {
            if ((KindOf(source) & kinds) == 0)
            {
                continue;
            }

            EffectInstance copy = source.Clone();
            copy.Id = ctx.Ids.NewId("effect");
            ids[source.Id] = copy.Id;
            foreach (Parameter parameter in copy.Parameters.Values)
            {
                parameter.Keyframes = map.FromOffsets(TrimToLength(parameter, clip.Duration));
            }

            MaskEdits.MapShapeKeys(copy, map.FromOffset);
            pasted.Add(copy);
        }

        // Una máscara que limita un efecto sigue a la copia de ese efecto.
        foreach (EffectInstance copy in pasted)
        {
            if (copy.Type == BuiltInEffects.Mask && copy.Options.GetValueOrDefault(BuiltInEffects.MaskOptions.Target) is { Length: > 0 } target)
            {
                copy.Options[BuiltInEffects.MaskOptions.Target] = ids.GetValueOrDefault(target, target);
            }
        }

        var result = new List<EffectInstance>(clip.Effects.Count + pasted.Count);
        bool inserted = false;
        foreach (EffectInstance effect in clip.Effects)
        {
            if ((KindOf(effect) & kinds) == 0)
            {
                result.Add(effect);
            }
            else if (!inserted)
            {
                result.AddRange(pasted);
                inserted = true;
            }
        }

        if (!inserted)
        {
            result.AddRange(pasted);
        }

        return result;
    }

    /// <summary>
    /// Los keyframes (en distancia al inicio) acotados a <c>[0, length]</c>: los que caen fuera del clip destino se quitan
    /// y en el borde que cruzaban entra uno con el valor que tenía la animación ahí, así lo que se ve dentro del clip no
    /// cambia (exacto con interpolación lineal o constante; con suavizado, el último tramo se acorta).
    /// </summary>
    internal static List<Keyframe> TrimToLength(Parameter parameter, Time length)
    {
        List<Keyframe> keys = parameter.Keyframes;
        if (keys.Count == 0 || (keys[0].Time >= Time.Zero && keys[^1].Time <= length))
        {
            return keys;
        }

        var result = new List<Keyframe>(keys.Count + 2);
        if (keys[0].Time < Time.Zero && keys.FindLast(k => k.Time < Time.Zero) is { } before && !keys.Exists(k => k.Time == Time.Zero))
        {
            result.Add(new Keyframe(Time.Zero, parameter.Evaluate(Time.Zero), before.Interpolation));
        }

        result.AddRange(keys.Where(k => k.Time >= Time.Zero && k.Time <= length));
        if (keys[^1].Time > length && !keys.Exists(k => k.Time == length))
        {
            result.Add(new Keyframe(length, parameter.Evaluate(length)));
        }

        return result;
    }

    // Una vez por grupo enlazado: el grupo comparte la velocidad.
    private static bool PasteSpeed(EditContext ctx, List<(Track Track, Clip Clip)> targets, ClipAttributes attributes, RippleScope scope)
    {
        bool changed = false;
        var groups = new HashSet<string>(StringComparer.Ordinal);
        foreach ((_, Clip clip) in targets)
        {
            if (clip.Generator is not null || clip.Speed is { Freeze: true } || (clip.LinkId is { } link && !groups.Add(link)))
            {
                continue;
            }

            ClipSpeed? speed = attributes.Speed;
            changed = true;
            if (SameSpeed(clip.Speed, speed))
            {
                // Ya la tiene: rehacer el mapeo podría mover el final del clip sin motivo.
                continue;
            }

            if (speed?.Curve is { } curve)
            {
                TimelineEdits.SetCurve(ctx, clip.Id, curve, scope);
            }
            else
            {
                TimelineEdits.SetSpeed(ctx, clip.Id, speed?.Rate ?? new Rational(1, 1), scope);
            }

            TimelineEdits.SetReverse(ctx, clip.Id, speed is { Reverse: true });

            // A 1× el origen no tiene velocidad: el destino vuelve al tono y al muestreo por defecto, no se queda con los suyos.
            TimelineEdits.SetPreservePitch(ctx, clip.Id, speed?.PreservePitch ?? true);
            FrameSampling sampling = speed?.FrameSampling ?? default;
            foreach ((_, Clip member) in ctx.Sequence.GetLinkGroup(clip.Id))
            {
                if (member.Speed is { } own)
                {
                    own.FrameSampling = sampling;
                }
            }
        }

        return changed;
    }

    private static bool SameSpeed(ClipSpeed? a, ClipSpeed? b) =>
        a is null || b is null ? (a ?? b) is not { } one || (one.IsIdentity && one.Offset == Time.Zero && one.PreservePitch && one.FrameSampling == default)
            : a.ContentEquals(b);
}

/// <summary>
/// Pasa los keyframes de un clip (en tiempo de sus efectos) a distancia desde su inicio en la línea de tiempo, y al revés.
/// Sin velocidad, o en un congelado, la cuenta es exacta y sin acotar; con velocidad se usa el mapeo del clip, acotado a
/// él, y dos keyframes que caigan en el mismo instante se quedan en uno (el último).
/// </summary>
internal readonly struct KeyframeMap
{
    private readonly Clip _clip;
    private readonly ClipTimeMap _map;
    private readonly bool _linear;

    public KeyframeMap(Clip clip)
    {
        _clip = clip;
        _map = clip.TimeMap;
        _linear = _map.IsIdentity || _map.Freeze;
    }

    private Time Origin => _map.EffectTime(_map.Start);

    public Time ToOffset(Time effectTime) =>
        _linear ? effectTime - Origin : _map.TimelineOf(effectTime) - _map.Start;

    public Time FromOffset(Time offset) =>
        _linear ? Origin + offset : _clip.ToEffectTime(_map.Start + offset);

    public List<Keyframe> FromOffsets(List<Keyframe> keyframes)
    {
        var byTime = new SortedDictionary<Time, Keyframe>();
        foreach (Keyframe keyframe in keyframes)
        {
            Time at = FromOffset(keyframe.Time);
            byTime[at] = keyframe with { Time = at };
        }

        return [.. byTime.Values];
    }
}
