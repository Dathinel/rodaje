using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Services.Inspector;

/// <summary>Edición atómica de la selección de la gráfica mediante el núcleo compartido.</summary>
public static class KeyframeGraphEdits
{
    public static SequenceEditCommand Move(string sequenceId, string clipId, string effectId, string name,
        IReadOnlyList<Keyframe> original, Time previousDelta, Time delta, double valueDelta, ParameterSpec spec, string? partnerName = null,
        RippleScope scope = RippleScope.Track) =>
        new("Mover keyframes", sequenceId, ctx =>
        {
            Time[] times = original.Select(key => key.Time + previousDelta).ToArray();
            if (effectId == SpeedGraph.EffectId)
            {
                double moved = Shift(original, valueDelta, spec);
                SpeedGraph.Edit(ctx, clipId, keys => keys.Where(key => !times.Contains(key.Time))
                    .Concat(original.Select(key => new Keyframe(key.Time + delta, key.Value + moved))), scope);
                return;
            }

            EffectEdits.MoveKeyframes(ctx, clipId, effectId, name, times, delta - previousDelta);
            if (partnerName is not null)
            {
                Parameter partner = ctx.Sequence.FindClip(clipId)!.Value.Clip.Effects.First(e => e.Id == effectId).Parameters[partnerName];
                Time[] paired = times.Where(time => partner.Keyframes.Exists(k => k.Time == time)).ToArray();
                if (paired.Length > 0)
                {
                    EffectEdits.MoveKeyframes(ctx, clipId, effectId, partnerName, paired, delta - previousDelta);
                }
            }

            double shift = Shift(original, valueDelta, spec);
            foreach (Keyframe key in original)
            {
                EffectEdits.SetKeyframe(ctx, clipId, effectId, name, key.Time + delta, key.Value + shift);
            }
        }, mergeKey: $"graph|{clipId}|{effectId}|{name}");

    public static SequenceEditCommand Delete(string sequenceId, string clipId, string effectId, string name, IEnumerable<Time> selected,
        RippleScope scope = RippleScope.Track)
    {
        ArgumentNullException.ThrowIfNull(selected);
        Time[] times = selected.ToArray();
        return new("Borrar keyframes", sequenceId, ctx =>
        {
            if (effectId == SpeedGraph.EffectId)
            {
                // Los extremos de la rampa no se quitan, como en el inspector de velocidad.
                SpeedGraph.Edit(ctx, clipId, keys => keys.Where((key, i) => i == 0 || i == keys.Count - 1 || !times.Contains(key.Time)), scope);
                return;
            }

            foreach (Time time in times)
            {
                EffectEdits.RemoveKeyframe(ctx, clipId, effectId, name, time);
            }
        });
    }

    public static SequenceEditCommand Paste(string sequenceId, string clipId, string effectId, string name, KeyframeClipboard clipboard, Time at,
        RippleScope scope = RippleScope.Track)
    {
        ArgumentNullException.ThrowIfNull(clipboard);
        if (effectId != SpeedGraph.EffectId)
        {
            return Edits.PasteKeyframes(sequenceId, clipId, effectId, name, clipboard, at);
        }

        return new("Pegar keyframes", sequenceId, ctx => SpeedGraph.Edit(ctx, clipId, keys =>
        {
            IReadOnlyList<Keyframe> pasted = clipboard.PasteAt(at);
            return keys.Where(key => !pasted.Any(p => p.Time == key.Time)).Concat(pasted);
        }, scope));
    }

    /// <summary>El cambio de valor que cabe en todos los keyframes sin salir del rango del modelo.</summary>
    private static double Shift(IReadOnlyList<Keyframe> original, double valueDelta, ParameterSpec spec) =>
        Math.Clamp(valueDelta, original.Max(key => spec.ModelMinimum - key.Value), original.Min(key => spec.ModelMaximum - key.Value));
}
