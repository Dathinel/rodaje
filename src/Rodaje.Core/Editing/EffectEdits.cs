using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Editing;

/// <summary>Ediciones de la pila de efectos de un clip y de sus parámetros animables.</summary>
/// <remarks>Los instantes de los keyframes van en tiempo del origen y deben caer dentro del clip.</remarks>
public static class EffectEdits
{
    /// <summary>Agrega un efecto al clip, al final de la pila o en <paramref name="index"/>.</summary>
    /// <returns>Identificador del efecto nuevo.</returns>
    public static string AddEffect(EditContext ctx, string clipId, string type, int? index = null)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        Clip clip = EditableClip(ctx, clipId);
        int at = index ?? clip.Effects.Count;
        if (at < 0 || at > clip.Effects.Count)
        {
            throw new EditException($"La posición {at} no existe: el clip tiene {clip.Effects.Count} efectos.");
        }

        var effect = new EffectInstance { Id = ctx.Ids.NewId("effect"), Type = type };
        clip.Effects.Insert(at, effect);
        return effect.Id;
    }

    /// <summary>Agrega el efecto si el clip no tiene ninguno de ese tipo.</summary>
    /// <returns>Identificador del efecto creado, o <c>null</c> si ya existía.</returns>
    public static string? EnsureEffect(EditContext ctx, string clipId, string type)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        Clip clip = EditableClip(ctx, clipId);
        return clip.Effects.Exists(e => e.Type == type) ? null : AddEffect(ctx, clipId, type);
    }

    public static void RemoveEffect(EditContext ctx, string clipId, string effectId)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        Clip clip = EditableClip(ctx, clipId);
        clip.Effects.Remove(FindEffect(clip, effectId));
    }

    /// <summary>Cambia la posición del efecto en la pila; el primero se aplica antes.</summary>
    public static void MoveEffect(EditContext ctx, string clipId, string effectId, int newIndex)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        Clip clip = EditableClip(ctx, clipId);
        EffectInstance effect = FindEffect(clip, effectId);
        if (newIndex < 0 || newIndex >= clip.Effects.Count)
        {
            throw new EditException($"La posición {newIndex} no existe: el clip tiene {clip.Effects.Count} efectos.");
        }

        clip.Effects.Remove(effect);
        clip.Effects.Insert(newIndex, effect);
    }

    public static void SetEffectEnabled(EditContext ctx, string clipId, string effectId, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        FindEffect(EditableClip(ctx, clipId), effectId).Enabled = enabled;
    }

    /// <summary>
    /// Cambia el parámetro como lo haría un deslizador: si está animado, fija (o crea) el keyframe en
    /// <paramref name="at"/>; si no, cambia su valor constante.
    /// </summary>
    public static void SetParameter(EditContext ctx, string clipId, string effectId, string name, double value, Time at)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        EnsureFinite(value, name);
        Clip clip = EditableClip(ctx, clipId);
        EffectInstance effect = FindEffect(clip, effectId);
        if (effect.Parameters.TryGetValue(name, out Parameter? parameter) && parameter.Keyframes.Count > 0)
        {
            EnsureInside(clip, at);
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

        Clip clip = EditableClip(ctx, clipId);
        EnsureInside(clip, at);
        EffectInstance effect = FindEffect(clip, effectId);
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
        Parameter parameter = FindParameter(FindEffect(EditableClip(ctx, clipId), effectId), name);
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
        EffectInstance effect = FindEffect(EditableClip(ctx, clipId), effectId);
        foreach ((string name, double value) in defaults)
        {
            EnsureFinite(value, name);
            if (effect.Parameters.TryGetValue(name, out Parameter? parameter))
            {
                parameter.Keyframes.Clear();
                parameter.Value = value;
            }
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
        Parameter parameter = FindParameter(FindEffect(EditableClip(ctx, clipId), effectId), name);
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

    private static Clip EditableClip(EditContext ctx, string clipId)
    {
        ArgumentNullException.ThrowIfNull(clipId);
        if (ctx.Sequence.FindClip(clipId) is not { } found)
        {
            throw new EditException($"El clip «{clipId}» ya no está en la secuencia.");
        }

        EditContext.EnsureUnlocked(found.Track);
        return found.Clip;
    }

    private static EffectInstance FindEffect(Clip clip, string effectId) =>
        clip.Effects.Find(e => e.Id == effectId)
        ?? throw new EditException($"El clip no tiene el efecto «{effectId}».");

    private static Parameter FindParameter(EffectInstance effect, string name) =>
        effect.Parameters.TryGetValue(name, out Parameter? parameter)
            ? parameter
            : throw new EditException($"El efecto no tiene el parámetro «{name}».");

    private static int IndexAt(Parameter parameter, Time at, string name)
    {
        int index = parameter.Keyframes.FindIndex(k => k.Time == at);
        return index >= 0 ? index : throw new EditException($"«{name}» no tiene un keyframe en {at}.");
    }

    private static void EnsureInside(Clip clip, Time at)
    {
        if (at < clip.SourceIn || at > clip.SourceOut)
        {
            throw new EditException("El keyframe tiene que quedar dentro del clip.");
        }
    }

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
