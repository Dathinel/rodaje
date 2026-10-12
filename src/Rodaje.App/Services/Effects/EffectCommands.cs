using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Packages.Content;

namespace Rodaje.App.Services.Effects;

/// <summary>
/// Ediciones de efectos que el núcleo no trae como comando suelto: opciones (modo de mezcla, LUT) y aplicar filtros y
/// efectos de paquete. Cada una es un solo paso del historial, con el efecto que falte creado en el mismo paso.
/// </summary>
public static class EffectCommands
{
    /// <summary>
    /// Fija la opción <paramref name="option"/> del efecto <paramref name="effectId"/> o, si es nulo, del primero activo de
    /// ese tipo (que se crea si el clip no lo tiene). Un valor vacío quita la opción.
    /// </summary>
    /// <remarks><see cref="SequenceEditCommand.CreatedIds"/>: el efecto creado, si hizo falta.</remarks>
    public static SequenceEditCommand SetOption(
        string sequenceId, string clipId, string effectType, string? effectId, string option, string value, string description)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new SequenceEditCommand(description, sequenceId, ctx =>
        {
            List<string> created = [];
            string target = effectId ?? "";
            if (effectId is null)
            {
                if (EffectEdits.EnsureEffect(ctx, clipId, effectType) is { } id)
                {
                    created.Add(id);
                }

                Clip clip = EditableClip(ctx, clipId);
                target = (clip.Effects.Find(e => e.Type == effectType && e.Enabled) ?? clip.Effects.Find(e => e.Type == effectType)!).Id;
            }

            EffectEdits.SetOption(ctx, clipId, target, option, value);
            return created;
        });
    }

    public static SequenceEditCommand SetBlendMode(string sequenceId, string clipId, string mode) =>
        SetOption(sequenceId, clipId, BuiltInEffects.Blend, null, BuiltInEffects.BlendOptions.Mode, mode, "Cambiar modo de mezcla");

    /// <summary>Pone (o quita, con vacío) la LUT del efecto de color indicado o del primero del clip.</summary>
    public static SequenceEditCommand SetLut(string sequenceId, string clipId, string? effectId, string reference) =>
        SetOption(sequenceId, clipId, BuiltInEffects.Color, effectId, BuiltInEffects.ColorOptions.Lut, reference,
            reference.Length == 0 ? "Quitar LUT" : "Cambiar LUT");

    /// <summary>
    /// Copia los efectos de un filtro de paquete al final de la pila, con <c>options.source</c> = <paramref name="reference"/>.
    /// Si el clip ya tenía ese mismo filtro, se reemplaza.
    /// </summary>
    /// <remarks><see cref="SequenceEditCommand.CreatedIds"/>: los efectos creados.</remarks>
    public static SequenceEditCommand ApplyFilter(string sequenceId, string clipId, FilterPreset preset, string reference)
    {
        ArgumentNullException.ThrowIfNull(preset);
        ArgumentException.ThrowIfNullOrEmpty(reference);
        return new SequenceEditCommand($"Aplicar filtro «{preset.Name}»", sequenceId, ctx =>
        {
            Clip clip = EditableClip(ctx, clipId);
            clip.Effects.RemoveAll(e => e.Options.TryGetValue(BuiltInEffects.CommonOptions.Source, out string? source) && source == reference);
            return [.. preset.Effects.Select(e => AddCopy(ctx, clip, e, reference).Id)];
        });
    }

    /// <summary>
    /// Copia un efecto de paquete: sus efectos al final de la pila y sus curvas como keyframes, al principio, al final o a lo
    /// largo del clip según <see cref="EffectPreset.Placement"/>. Si la animación es más larga que el clip, se encoge. Las
    /// posiciones se escalan del tamaño de referencia al de la secuencia. Los tiempos de la animación son de la línea de
    /// tiempo: a 2× una entrada de 1 s sigue durando 1 s en pantalla.
    /// </summary>
    public static SequenceEditCommand ApplyEffect(string sequenceId, string clipId, EffectPreset preset, string reference)
    {
        ArgumentNullException.ThrowIfNull(preset);
        ArgumentException.ThrowIfNullOrEmpty(reference);
        return new SequenceEditCommand($"Aplicar efecto «{preset.Name}»", sequenceId, ctx =>
        {
            Clip clip = EditableClip(ctx, clipId);
            clip.Effects.RemoveAll(e => e.Options.TryGetValue(BuiltInEffects.CommonOptions.Source, out string? source) && source == reference);
            List<EffectInstance> added = [.. preset.Effects.Select(e => AddCopy(ctx, clip, e, reference))];
            (Time start, double scale) = Placement(clip, preset);
            double scaleX = preset.ReferenceWidth > 0 ? (double)ctx.Sequence.Width / preset.ReferenceWidth : 1;
            double scaleY = preset.ReferenceHeight > 0 ? (double)ctx.Sequence.Height / preset.ReferenceHeight : 1;
            foreach (PresetCurve curve in preset.Curves.Where(c => c.Keys.Count > 0))
            {
                EffectInstance? effect = added.Find(e => e.Type == curve.Effect);
                if (effect is null)
                {
                    effect = NewEffect(ctx, clip, curve.Effect, reference);
                    added.Add(effect);
                }

                double factor = curve.Effect == BuiltInEffects.Transform
                    ? curve.Parameter switch
                    {
                        BuiltInEffects.TransformParameters.X => scaleX,
                        BuiltInEffects.TransformParameters.Y => scaleY,
                        _ => 1,
                    }
                    : 1;
                ParameterDefinition? definition = EffectRegistry.Find(curve.Effect)?.FindParameter(curve.Parameter);
                var keys = new SortedDictionary<Time, Keyframe>();
                foreach (PresetKey key in curve.Keys)
                {
                    Time at = KeyframeTime(clip, start + Time.FromSeconds(Math.Max(0, key.T) * scale));
                    double value = key.Value * factor;
                    value = definition?.Clamp(value) ?? value;
                    keys[at] = new Keyframe(at, value, EffectContent.ToInterpolation(key.Interpolation))
                    {
                        Ease = key.Ease is { } e ? new BezierEase(e.X1, e.Y1, e.X2, e.Y2).Clamped() : null,
                    };
                }

                List<Keyframe> list = [.. keys.Values];
                effect.Parameters[curve.Parameter] = new Parameter { Value = list[0].Value, Keyframes = list };
            }

            return [.. added.Select(e => e.Id)];
        });
    }

    /// <summary>Dónde empieza la animación (instante de la línea de tiempo) y cuánto se encoge para caber en el clip.</summary>
    public static (Time Start, double Scale) Placement(Clip clip, EffectPreset preset)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(preset);
        double clipSeconds = clip.Duration.ToSeconds();
        double duration = preset.DurationSeconds > 0 ? preset.DurationSeconds : clipSeconds;
        double scale = preset.Placement == EffectPlacement.Whole || duration > clipSeconds ? clipSeconds / duration : 1;
        Time start = preset.Placement == EffectPlacement.Out
            ? clip.End - Time.FromSeconds(duration * scale)
            : clip.Start;
        return (Time.Max(clip.Start, start), scale);
    }

    /// <summary>
    /// Instante de los keyframes del clip (<see cref="Clip.ToEffectTime"/>) para un instante de la línea de tiempo, acotado a
    /// <c>[Start, End]</c> con el final incluido: ahí va el último keyframe de una animación que llega al borde.
    /// </summary>
    public static Time KeyframeTime(Clip clip, Time timeline)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (timeline < clip.End)
        {
            return clip.ToEffectTime(timeline);
        }

        ClipTimeMap map = clip.TimeMap;
        if (map.Freeze)
        {
            return map.EffectTime(clip.End) + Time.FromTicks(1);
        }

        return Time.Max(clip.SourceIn, Time.Min(clip.ToSourceTime(clip.End), clip.SourceOut));
    }

    /// <summary>El instante de la línea de tiempo de un keyframe del clip, dentro del clip (la inversa de <see cref="KeyframeTime"/>).</summary>
    public static Time TimelineOfKeyframe(Clip clip, Time keyframeTime)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ClipTimeMap map = clip.TimeMap;
        if (!map.Freeze)
        {
            return map.TimelineOf(keyframeTime);
        }

        Time virtualStart = map.EffectTime(clip.Start);
        Time at = clip.Start + (keyframeTime - virtualStart);
        return Time.Max(clip.Start, Time.Min(at, clip.End));
    }

    private static EffectInstance AddCopy(EditContext ctx, Clip clip, PresetEffect source, string reference)
    {
        EffectInstance effect = NewEffect(ctx, clip, source.Type, reference);
        EffectDefinition? definition = EffectRegistry.Find(source.Type);
        foreach ((string name, double value) in source.Parameters)
        {
            if (double.IsFinite(value))
            {
                effect.Parameters[name] = new Parameter { Value = definition?.FindParameter(name)?.Clamp(value) ?? value };
            }
        }

        foreach ((string name, string value) in source.Options)
        {
            if (name != BuiltInEffects.CommonOptions.Source && (definition?.FindOption(name) is not { } option || option.Accepts(value)))
            {
                effect.Options[name] = value;
            }
        }

        return effect;
    }

    private static EffectInstance NewEffect(EditContext ctx, Clip clip, string type, string reference)
    {
        string id = EffectEdits.AddEffect(ctx, clip.Id, type);
        EffectInstance effect = clip.Effects.Find(e => e.Id == id)!;
        effect.Options[BuiltInEffects.CommonOptions.Source] = reference;
        return effect;
    }

    private static Clip EditableClip(EditContext ctx, string clipId)
    {
        if (ctx.Sequence.FindClip(clipId) is not { } found)
        {
            throw new EditException("El clip ya no está en la secuencia.");
        }

        EditContext.EnsureUnlocked(found.Track);
        return found.Clip;
    }
}
