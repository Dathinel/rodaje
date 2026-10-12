using Rodaje.App.Services.Effects;
using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using T = Rodaje.Core.Model.BuiltInEffects.TransformParameters;

namespace Rodaje.App.Services.Inspector;

/// <summary>Una animación de fábrica sobre la transformación del clip, en un solo paso del historial.</summary>
public static class MotionPresetCommands
{
    /// <summary>
    /// Reemplaza los keyframes de los parámetros que toca la animación (los demás quedan como estaban) partiendo de la
    /// transformación que el clip tiene en su primer cuadro. Los segundos de la animación son de la línea de tiempo y se
    /// llevan al tiempo de los keyframes del clip (<see cref="EffectCommands.KeyframeTime"/>).
    /// </summary>
    public static SequenceEditCommand Apply(string sequenceId, string clipId, string presetId)
    {
        MotionPreset preset = MotionPresets.Find(presetId) ?? throw new ArgumentException($"No hay una animación «{presetId}».", nameof(presetId));
        return new SequenceEditCommand($"Animación «{preset.Name}»", sequenceId, ctx =>
        {
            List<string> created = [];
            if (EffectEdits.EnsureEffect(ctx, clipId, BuiltInEffects.Transform) is { } id)
            {
                created.Add(id);
            }

            Clip clip = ctx.Sequence.GetClip(clipId).Clip;
            EffectInstance effect = clip.Effects.Find(e => e.Type == BuiltInEffects.Transform && e.Enabled)
                ?? clip.Effects.Find(e => e.Type == BuiltInEffects.Transform)!;
            Time first = EffectCommands.KeyframeTime(clip, clip.Start);
            var from = new MotionBase(Value(effect, T.X, first), Value(effect, T.Y, first), Value(effect, T.Scale, first), Value(effect, T.Rotation, first));
            IReadOnlyDictionary<string, IReadOnlyList<MotionKey>> keys =
                MotionPresets.Build(presetId, ctx.Sequence.Width, ctx.Sequence.Height, clip.Duration.ToSeconds(), from);
            foreach ((string name, IReadOnlyList<MotionKey> list) in keys)
            {
                EffectEdits.ReplaceKeyframes(ctx, clipId, effect.Id, name, ToClip(clip, list));
            }

            return created;
        });
    }

    /// <summary>Los keyframes en el tiempo del clip; si dos caen en el mismo instante (un clip muy corto o muy rápido) gana el último: el destino de la animación.</summary>
    internal static List<Keyframe> ToClip(Clip clip, IReadOnlyList<MotionKey> keys)
    {
        var list = new List<Keyframe>(keys.Count);
        foreach (MotionKey key in keys)
        {
            Time at = EffectCommands.KeyframeTime(clip, clip.Start + Time.FromSeconds(key.Seconds));
            var keyframe = new Keyframe(at, key.Value, key.Interpolation) { Ease = key.Ease };
            if (list.Count > 0 && at <= list[^1].Time)
            {
                list[^1] = keyframe with { Time = list[^1].Time };
                continue;
            }

            list.Add(keyframe);
        }

        return list;
    }

    /// <summary>
    /// Para la miniatura: la animación en un cuadro de <paramref name="width"/>×<paramref name="height"/> durante 2 s,
    /// muestreada en <paramref name="samples"/> instantes (posición desde el centro, escala y giro).
    /// </summary>
    public static IReadOnlyList<(double X, double Y, double Scale, double Rotation)> Preview(string presetId, int width, int height, int samples)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(samples, 2);
        const double Seconds = 2;
        IReadOnlyDictionary<string, IReadOnlyList<MotionKey>> keys = MotionPresets.Build(presetId, width, height, Seconds, MotionBase.Default);
        Parameter P(string name) => keys.TryGetValue(name, out IReadOnlyList<MotionKey>? list)
            ? new Parameter
            {
                Value = list[0].Value,
                Keyframes = list.Count < 2 ? [] : [.. list.Select(k => new Keyframe(Time.FromSeconds(k.Seconds), k.Value, k.Interpolation) { Ease = k.Ease })],
            }
            : new Parameter { Value = T.DefaultValue(name) };
        Parameter x = P(T.X);
        Parameter y = P(T.Y);
        Parameter scale = P(T.Scale);
        Parameter rotation = P(T.Rotation);
        var frames = new List<(double, double, double, double)>(samples);
        for (int i = 0; i < samples; i++)
        {
            Time at = Time.FromSeconds(Seconds * i / (samples - 1));
            frames.Add((x.Evaluate(at), y.Evaluate(at), scale.Evaluate(at), rotation.Evaluate(at)));
        }

        return frames;
    }

    private static double Value(EffectInstance effect, string name, Time at) =>
        effect.Parameters.TryGetValue(name, out Parameter? parameter) ? parameter.Evaluate(at) : T.DefaultValue(name);
}
