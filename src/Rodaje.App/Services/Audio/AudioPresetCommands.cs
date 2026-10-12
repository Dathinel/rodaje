using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;

namespace Rodaje.App.Services.Audio;

/// <summary>Los preajustes de ecualizador y compresor (<see cref="AudioPresets"/>) sobre un clip o una pista, en un paso.</summary>
internal static class AudioPresetCommands
{
    /// <summary>Deja el ecualizador y el compresor del clip o la pista como el preajuste; nulo quita los dos.</summary>
    public static SequenceEditCommand Apply(string sequenceId, string ownerId, AudioPreset? preset) =>
        new(preset is null ? "Quitar ecualizador y compresor" : $"Preajuste de audio «{preset.Name}»", sequenceId, ctx =>
        {
            EffectEdits.SetEffectValues(ctx, ownerId, BuiltInEffects.Equalizer, preset?.Equalizer);
            EffectEdits.SetEffectValues(ctx, ownerId, BuiltInEffects.Compressor, preset?.Compressor);
        });

    /// <summary>El preajuste con el que coinciden los efectos; nulo si no hay ecualizador ni compresor activos o si se tocaron a mano.</summary>
    public static AudioPreset? Match(IReadOnlyList<EffectInstance> effects, out bool hasEffects)
    {
        ArgumentNullException.ThrowIfNull(effects);
        EffectInstance? eq = effects.FirstOrDefault(e => e.Enabled && e.Type == BuiltInEffects.Equalizer);
        EffectInstance? compressor = effects.FirstOrDefault(e => e.Enabled && e.Type == BuiltInEffects.Compressor);
        hasEffects = eq is not null || compressor is not null;
        return hasEffects ? AudioPresets.All.FirstOrDefault(p => Same(eq, p.Equalizer) && Same(compressor, p.Compressor)) : null;
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
