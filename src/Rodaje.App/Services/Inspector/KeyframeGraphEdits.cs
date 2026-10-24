using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Services.Inspector;

/// <summary>Edición atómica de la selección de la gráfica mediante el núcleo compartido.</summary>
public static class KeyframeGraphEdits
{
    public static SequenceEditCommand Move(string sequenceId, string clipId, string effectId, string name,
        IReadOnlyList<Keyframe> original, Time previousDelta, Time delta, double valueDelta, ParameterSpec spec, string? partnerName = null) =>
        new("Mover keyframes", sequenceId, ctx =>
        {
            Time[] times = original.Select(key => key.Time + previousDelta).ToArray();
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

            double low = original.Max(key => spec.ModelMinimum - key.Value);
            double high = original.Min(key => spec.ModelMaximum - key.Value);
            double shift = Math.Clamp(valueDelta, low, high);
            foreach (Keyframe key in original)
            {
                EffectEdits.SetKeyframe(ctx, clipId, effectId, name, key.Time + delta, key.Value + shift);
            }
        }, mergeKey: $"graph|{clipId}|{effectId}|{name}");

    public static SequenceEditCommand Delete(string sequenceId, string clipId, string effectId, string name, IEnumerable<Time> selected)
    {
        ArgumentNullException.ThrowIfNull(selected);
        Time[] times = selected.ToArray();
        return new("Borrar keyframes", sequenceId, ctx =>
        {
            foreach (Time time in times)
            {
                EffectEdits.RemoveKeyframe(ctx, clipId, effectId, name, time);
            }
        });
    }
}
