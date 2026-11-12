using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;

namespace Rodaje.App.Services.Audio;

/// <summary>Los preajustes de ecualizador y compresor (<see cref="AudioPresets"/>) sobre un clip o una pista, en un paso.</summary>
internal static class AudioPresetCommands
{
    /// <summary>
    /// Deja el compresor (y el ecualizador, si el preajuste trae uno) del clip, la pista o la mezcla como el preajuste; nulo
    /// quita los dos. El dueño es el identificador del clip, de la pista o de la secuencia (la mezcla).
    /// </summary>
    public static SequenceEditCommand Apply(string sequenceId, string ownerId, AudioPreset? preset) =>
        Apply(sequenceId, [ownerId], preset);

    /// <summary>
    /// Deja el compresor (y el ecualizador) de varios dueños como el preajuste en un solo paso del historial.
    /// </summary>
    public static SequenceEditCommand Apply(string sequenceId, IEnumerable<string> ownerIds, AudioPreset? preset) =>
        new(preset is null ? "Quitar ecualizador y compresor" : $"Preajuste de audio «{preset.Name}»", sequenceId, ctx =>
        {
            foreach (string ownerId in ownerIds)
            {
                if (preset is null || preset.Equalizer is not null)
                {
                    EffectEdits.SetEffectValues(ctx, ownerId, BuiltInEffects.Equalizer, preset?.Equalizer);
                }

                EffectEdits.SetEffectValues(ctx, ownerId, BuiltInEffects.Compressor, preset?.Compressor);

                // Los efectos suenan en el orden de la pila: el ecualizador del preajuste va antes que su compresor.
                List<EffectInstance> effects = ctx.Sequence.FindClip(ownerId)?.Clip.Effects ?? ctx.Sequence.FindTrack(ownerId)?.Effects
                    ?? ctx.Sequence.FindBus(ownerId)?.Effects ?? ctx.Sequence.MasterEffects;
                int eq = effects.FindIndex(e => e.Type == BuiltInEffects.Equalizer);
                int compressor = effects.FindIndex(e => e.Type == BuiltInEffects.Compressor);
                if (eq > compressor && compressor >= 0)
                {
                    EffectEdits.MoveEffect(ctx, ownerId, effects[eq].Id, compressor);
                }
            }
        });
    /// <summary>
    /// El preajuste con el que coinciden los efectos (el ecualizador solo cuenta si el preajuste trae uno); nulo si no hay
    /// ecualizador ni compresor activos o si se tocaron a mano.
    /// </summary>
    public static AudioPreset? Match(IReadOnlyList<EffectInstance> effects, out bool hasEffects)
    {
        ArgumentNullException.ThrowIfNull(effects);
        EffectInstance? eq = effects.FirstOrDefault(e => e.Enabled && e.Type == BuiltInEffects.Equalizer);
        EffectInstance? compressor = effects.FirstOrDefault(e => e.Enabled && e.Type == BuiltInEffects.Compressor);
        hasEffects = eq is not null || compressor is not null;
        return hasEffects ? AudioPresets.All.FirstOrDefault(p => (p.Equalizer is null || Same(eq, p.Equalizer)) && Same(compressor, p.Compressor)) : null;
    }

    private static bool Same(EffectInstance? effect, IReadOnlyDictionary<string, double>? values)
    {
        if (effect is null || values is null)
        {
            return effect is null && values is null;
        }

        EffectDefinition definition = EffectRegistry.Find(effect.Type)!;
        return definition.Parameters.All(p =>
        {
            double expected = values.TryGetValue(p.Name, out double v) ? v : p.DefaultValue;
            double actual = effect.Parameters.TryGetValue(p.Name, out Parameter? parameter) ? parameter.Value : p.DefaultValue;
            return Math.Abs(expected - actual) < 1e-6;
        });
    }
}
