using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Editing;

/// <summary>Ediciones de la pila de efectos de un clip o de una pista y de sus parámetros animables.</summary>
/// <remarks>
/// <c>clipId</c> puede ser también el de una pista (los identificadores son únicos en el proyecto) o el de la secuencia, para
/// los efectos de toda la mezcla (<see cref="Sequence.MasterEffects"/>). Los keyframes de un clip van en tiempo del origen y
/// deben caer dentro de él; los de una pista o la mezcla, en tiempo de la secuencia y desde cero.
/// </remarks>
public static class EffectEdits
{
    /// <summary>Agrega un efecto al clip o a la pista, al final de la pila o en <paramref name="index"/>.</summary>
    /// <remarks>
    /// Una transición no es un efecto. Un clip admite tipos desconocidos (se conservan sin aplicarse); una pista solo
    /// tipos registrados de su clase: de audio en una de audio, de video en una de video.
    /// </remarks>
    /// <returns>Identificador del efecto nuevo.</returns>
    public static string AddEffect(EditContext ctx, string clipId, string type, int? index = null)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        Host host = EditableHost(ctx, clipId);
        EnsureApplicable(host, type);
        List<EffectInstance> effects = host.Effects;
        int at = index ?? effects.Count;
        if (at < 0 || at > effects.Count)
        {
            throw new EditException($"La posición {at} no existe: hay {effects.Count} efectos.");
        }

        var effect = new EffectInstance { Id = ctx.Ids.NewId("effect"), Type = type };
        effects.Insert(at, effect);
        return effect.Id;
    }

    /// <summary>Agrega el efecto si el clip no tiene ninguno de ese tipo.</summary>
    /// <returns>Identificador del efecto creado, o <c>null</c> si ya existía.</returns>
    public static string? EnsureEffect(EditContext ctx, string clipId, string type)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        return EditableHost(ctx, clipId).Effects.Exists(e => e.Type == type) ? null : AddEffect(ctx, clipId, type);
    }

    public static void RemoveEffect(EditContext ctx, string clipId, string effectId)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        Host host = EditableHost(ctx, clipId);
        host.Effects.Remove(FindEffect(host, effectId));
    }

    /// <summary>Cambia la posición del efecto en la pila; el primero se aplica antes.</summary>
    public static void MoveEffect(EditContext ctx, string clipId, string effectId, int newIndex)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        Host host = EditableHost(ctx, clipId);
        EffectInstance effect = FindEffect(host, effectId);
        if (newIndex < 0 || newIndex >= host.Effects.Count)
        {
            throw new EditException($"La posición {newIndex} no existe: hay {host.Effects.Count} efectos.");
        }

        host.Effects.Remove(effect);
        host.Effects.Insert(newIndex, effect);
    }

    public static void SetEffectEnabled(EditContext ctx, string clipId, string effectId, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        FindEffect(EditableHost(ctx, clipId), effectId).Enabled = enabled;
    }

    /// <summary>Fija una opción registrada del efecto (modo de mezcla, LUT, origen); un valor vacío la quita.</summary>
    public static void SetOption(EditContext ctx, string clipId, string effectId, string name, string value)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(value);
        EffectInstance effect = FindEffect(EditableHost(ctx, clipId), effectId);
        OptionDefinition definition = EffectRegistry.Find(effect.Type)?.FindOption(name)
            ?? (name == BuiltInEffects.CommonOptions.Source ? OptionDefinition.Reference(name) : null)
            ?? throw new EditException($"El efecto no tiene la opción «{name}».");
        if (value.Length == 0)
        {
            effect.Options.Remove(name);
        }
        else if (definition.Accepts(value))
        {
            effect.Options[name] = value;
        }
        else
        {
            throw new EditException($"«{value}» no es un valor de «{name}».");
        }
    }

    /// <summary>
    /// Cambia el parámetro como lo haría un deslizador: si está animado, fija (o crea) el keyframe en
    /// <paramref name="at"/>; si no, cambia su valor constante.
    /// </summary>
    public static void SetParameter(EditContext ctx, string clipId, string effectId, string name, double value, Time at)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        EnsureFinite(value, name);
        Host host = EditableHost(ctx, clipId);
        EffectInstance effect = FindEffect(host, effectId);
        value = Limit(effect, name, value);
        if (effect.Parameters.TryGetValue(name, out Parameter? parameter) && parameter.Keyframes.Count > 0)
        {
            EnsureInside(host, at);
            Upsert(parameter, at, value, null);
            return;
        }

        if (parameter is null)
        {
            effect.Parameters[name] = new Parameter { Value = value };
        }
        else
        {
            parameter.Value = value;
        }
    }

    /// <summary>Crea el keyframe en <paramref name="at"/> o cambia el que ya hay ahí.</summary>
    /// <remarks>
    /// <paramref name="interpolation"/> es la curva hacia el keyframe siguiente; si es <c>null</c>, el existente conserva
    /// la suya y uno nuevo toma la del vecino anterior (o lineal si no hay ninguno).
    /// </remarks>
    public static void SetKeyframe(
        EditContext ctx,
        string clipId,
        string effectId,
        string name,
        Time at,
        double value,
        Interpolation? interpolation = null)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        EnsureFinite(value, name);
        if (interpolation is { } curve)
        {
            EnsureDefined(curve);
        }

        Host host = EditableHost(ctx, clipId);
        EnsureInside(host, at);
        EffectInstance effect = FindEffect(host, effectId);
        value = Limit(effect, name, value);
        if (!effect.Parameters.TryGetValue(name, out Parameter? parameter))
        {
            parameter = new Parameter { Value = value };
            effect.Parameters[name] = parameter;
        }

        Upsert(parameter, at, value, interpolation);
    }

    /// <summary>Quita el keyframe de <paramref name="at"/>; si era el último, el parámetro queda fijo en su valor.</summary>
    public static void RemoveKeyframe(EditContext ctx, string clipId, string effectId, string name, Time at)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        Parameter parameter = FindParameter(FindEffect(EditableHost(ctx, clipId), effectId), name);
        int index = IndexAt(parameter, at, name);
        Keyframe removed = parameter.Keyframes[index];
        parameter.Keyframes.RemoveAt(index);
        if (parameter.Keyframes.Count == 0)
        {
            parameter.Value = removed.Value;
        }
    }

    /// <summary>
    /// Vuelve cada parámetro de <paramref name="defaults"/> a su valor por defecto y le quita los keyframes. Un parámetro
    /// que el efecto no tiene ya vale lo de fábrica y se deja como está.
    /// </summary>
    public static void ResetParameters(EditContext ctx, string clipId, string effectId, IReadOnlyDictionary<string, double> defaults)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(defaults);
        EffectInstance effect = FindEffect(EditableHost(ctx, clipId), effectId);
        foreach ((string name, double value) in defaults)
        {
            EnsureFinite(value, name);
            if (effect.Parameters.TryGetValue(name, out Parameter? parameter))
            {
                parameter.Keyframes.Clear();
                parameter.Value = Limit(effect, name, value);
            }
        }
    }

    /// <summary>
    /// Sube o baja el volumen (<c>rodaje.audio</c>) del clip o de la pista en <paramref name="deltaDb"/>: su valor y cada
    /// keyframe, así la envolvente conserva su forma. Crea el efecto si falta. Lo que pase del rango queda en el borde.
    /// </summary>
    public static void ShiftGain(EditContext ctx, string clipId, double deltaDb)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        EnsureFinite(deltaDb, BuiltInEffects.AudioParameters.GainDb);
        Host host = EditableHost(ctx, clipId);
        if (host.Kind != TrackKind.Audio && host.Clip is null)
        {
            throw new EditException("El volumen solo se cambia en clips o pistas de audio.");
        }

        EffectInstance effect = host.Effects.Find(e => e.Type == BuiltInEffects.Audio)
            ?? FindEffect(host, AddEffect(ctx, clipId, BuiltInEffects.Audio));
        effect.Enabled = true;
        string name = BuiltInEffects.AudioParameters.GainDb;
        if (!effect.Parameters.TryGetValue(name, out Parameter? gain))
        {
            gain = new Parameter { Value = EffectRegistry.DefaultValue(BuiltInEffects.Audio, name) };
            effect.Parameters[name] = gain;
        }

        gain.Value = Limit(effect, name, gain.Value + deltaDb);
        for (int i = 0; i < gain.Keyframes.Count; i++)
        {
            Keyframe key = gain.Keyframes[i];
            gain.Keyframes[i] = key with { Value = Limit(effect, name, key.Value + deltaDb) };
        }
    }

    /// <summary>
    /// Deja el primer efecto de <paramref name="type"/> del clip o la pista con exactamente esos valores constantes (sin
    /// keyframes) y activado, creándolo si falta; con <paramref name="values"/> nulo quita los de ese tipo. Para aplicar un
    /// preajuste en un paso.
    /// </summary>
    public static void SetEffectValues(EditContext ctx, string clipId, string type, IReadOnlyDictionary<string, double>? values)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        Host host = EditableHost(ctx, clipId);
        if (values is null)
        {
            host.Effects.RemoveAll(e => e.Type == type);
            return;
        }

        EffectInstance effect = host.Effects.Find(e => e.Type == type) ?? FindEffect(host, AddEffect(ctx, clipId, type));
        effect.Enabled = true;
        effect.Parameters.Clear();
        foreach ((string name, double value) in values)
        {
            EnsureFinite(value, name);
            if (EffectRegistry.Find(type)?.FindParameter(name) is null)
            {
                throw new EditException($"«{type}» no tiene el parámetro «{name}».");
            }

            effect.Parameters[name] = new Parameter { Value = Limit(effect, name, value) };
        }
    }

    /// <summary>Cambia la curva que va del keyframe de <paramref name="at"/> al siguiente.</summary>
    public static void SetKeyframeInterpolation(
        EditContext ctx,
        string clipId,
        string effectId,
        string name,
        Time at,
        Interpolation interpolation)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        EnsureDefined(interpolation);
        Parameter parameter = FindParameter(FindEffect(EditableHost(ctx, clipId), effectId), name);
        int index = IndexAt(parameter, at, name);
        parameter.Keyframes[index] = parameter.Keyframes[index] with { Interpolation = interpolation };
    }

    private static void Upsert(Parameter parameter, Time at, double value, Interpolation? interpolation)
    {
        List<Keyframe> keys = parameter.Keyframes;
        int index = keys.FindIndex(k => k.Time >= at);
        if (index >= 0 && keys[index].Time == at)
        {
            keys[index] = new Keyframe(at, value, interpolation ?? keys[index].Interpolation);
            return;
        }

        Interpolation inherited = interpolation ?? (index switch
        {
            0 => keys[0].Interpolation,
            < 0 => keys.Count > 0 ? keys[^1].Interpolation : Interpolation.Linear,
            _ => keys[index - 1].Interpolation,
        });
        keys.Insert(index < 0 ? keys.Count : index, new Keyframe(at, value, inherited));
    }

    /// <summary>Dueño de una pila de efectos: un clip (<see cref="Clip"/> no nulo), una pista entera o la mezcla (<see cref="Master"/>).</summary>
    private sealed record Host(Track? Track, Clip? Clip, List<EffectInstance>? Master = null)
    {
        public List<EffectInstance> Effects => Master ?? Clip?.Effects ?? Track!.Effects;

        public TrackKind Kind => Track?.Kind ?? TrackKind.Audio;
    }

    private static Host EditableHost(EditContext ctx, string clipId)
    {
        ArgumentNullException.ThrowIfNull(clipId);
        Host host = ctx.Sequence.FindClip(clipId) is { } found
            ? new Host(found.Track, found.Clip)
            : ctx.Sequence.FindTrack(clipId) is { } track
                ? new Host(track, null)
                : clipId == ctx.Sequence.Id
                    ? new Host(null, null, ctx.Sequence.MasterEffects)
                    : throw new EditException($"El clip «{clipId}» ya no está en la secuencia.");
        if (host.Track is { } owner)
        {
            EditContext.EnsureUnlocked(owner);
        }
        return host;
    }

    private static void EnsureApplicable(Host host, string type)
    {
        EffectDefinition? definition = EffectRegistry.Find(type);
        if (definition is { IsTransition: true })
        {
            throw new EditException($"«{type}» es una transición: va en un corte, no en la pila de efectos.");
        }

        EffectTarget target = host.Kind == TrackKind.Video ? EffectTarget.Video : EffectTarget.Audio;
        if (host.Clip is null && (definition is null || (definition.Target & target) == 0))
        {
            throw new EditException(host.Track is null ? $"«{type}» no se puede aplicar a la mezcla." : $"«{type}» no se puede aplicar a una pista de {TrackOps.KindName(host.Kind)}.");
        }
    }

    private static EffectInstance FindEffect(Host host, string effectId) =>
        host.Effects.Find(e => e.Id == effectId)
        ?? throw new EditException($"No hay un efecto «{effectId}» ahí.");

    private static Parameter FindParameter(EffectInstance effect, string name) =>
        effect.Parameters.TryGetValue(name, out Parameter? parameter)
            ? parameter
            : throw new EditException($"El efecto no tiene el parámetro «{name}».");

    private static int IndexAt(Parameter parameter, Time at, string name)
    {
        int index = parameter.Keyframes.FindIndex(k => k.Time == at);
        return index >= 0 ? index : throw new EditException($"«{name}» no tiene un keyframe en {at}.");
    }

    private static void EnsureInside(Host host, Time at)
    {
        if (host.Clip is not { } clip)
        {
            if (at < Time.Zero)
            {
                throw new EditException("El keyframe de una pista no puede quedar antes del inicio.");
            }
        }
        else
        {
            // En un congelado los keyframes van en un tiempo virtual que avanza con el clip.
            ClipTimeMap map = clip.TimeMap;
            (Time first, Time last) = map.Freeze
                ? (map.EffectTime(map.Start), map.EffectTime(map.Start) + map.Duration)
                : (map.SourceIn, map.SourceOut);
            if (at < first || at > last)
            {
                throw new EditException("El keyframe tiene que quedar dentro del clip.");
            }
        }
    }

    /// <summary>Un parámetro registrado queda dentro de sus límites, como lo exige <see cref="ModelValidator"/> al abrir.</summary>
    private static double Limit(EffectInstance effect, string name, double value) =>
        EffectRegistry.Find(effect.Type)?.FindParameter(name)?.Clamp(value) ?? value;

    private static void EnsureFinite(double value, string name)
    {
        if (!double.IsFinite(value))
        {
            throw new EditException($"«{name}» necesita un número.");
        }
    }

    private static void EnsureDefined(Interpolation interpolation)
    {
        if (!Enum.IsDefined(interpolation))
        {
            throw new EditException("Esa interpolación no existe.");
        }
    }
}
