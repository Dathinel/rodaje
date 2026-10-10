using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using P = Rodaje.Core.Model.BuiltInEffects.TransformParameters;

namespace Rodaje.Visor;

/// <summary>Comandos deshacibles que cambian el efecto <c>rodaje.transform</c> de un clip.</summary>
/// <remarks>Pulsaciones seguidas sobre el mismo clip y el mismo parámetro se funden en un solo paso de deshacer.</remarks>
internal static class LayerEdits
{
    public const double MoveStep = 20;
    public const double ScaleFactor = 1.1;
    public const double RotationStep = 15;

    private static readonly double[] OpacityCycle = [1, 0.75, 0.5, 0.25];

    public static SequenceEditCommand Move(string sequenceId, string clipId, double dx, double dy) =>
        new("Mover capa", sequenceId, ctx =>
        {
            EffectInstance effect = TransformOf(ctx, clipId);
            Set(effect, P.X, Get(effect, P.X) + dx);
            Set(effect, P.Y, Get(effect, P.Y) + dy);
        }, mergeKey: $"transform:{clipId}:posición");

    public static SequenceEditCommand Scale(string sequenceId, string clipId, double factor) =>
        Change("Escalar capa", sequenceId, clipId, P.Scale, v => Math.Clamp(v * factor, 0.05, 4));

    public static SequenceEditCommand Rotate(string sequenceId, string clipId, double degrees) =>
        Change("Girar capa", sequenceId, clipId, P.Rotation, v => (v + degrees) % 360);

    /// <summary>Pasa a la siguiente opacidad de 1 → 0,75 → 0,5 → 0,25 → 1.</summary>
    public static SequenceEditCommand CycleOpacity(string sequenceId, string clipId) =>
        Change("Opacidad de la capa", sequenceId, clipId, P.Opacity, NextOpacity);

    internal static double NextOpacity(double current) =>
        OpacityCycle.FirstOrDefault(o => o < current - 0.01, OpacityCycle[0]);

    /// <summary>Valor constante del parámetro, o su valor por defecto si el clip no tiene transformación.</summary>
    public static double Read(Clip clip, string parameter)
    {
        EffectInstance? effect = clip.Effects.Find(e => e.Type == BuiltInEffects.Transform);
        return effect is null ? Default(parameter) : Get(effect, parameter);
    }

    private static SequenceEditCommand Change(string description, string sequenceId, string clipId, string parameter, Func<double, double> change) =>
        new(description, sequenceId, ctx =>
        {
            EffectInstance effect = TransformOf(ctx, clipId);
            Set(effect, parameter, change(Get(effect, parameter)));
        }, mergeKey: $"transform:{clipId}:{parameter}");

    private static EffectInstance TransformOf(EditContext ctx, string clipId)
    {
        (Track track, Clip clip) = ctx.Sequence.GetClip(clipId);
        EditContext.EnsureUnlocked(track);
        EffectInstance? effect = clip.Effects.Find(e => e.Type == BuiltInEffects.Transform);
        if (effect is null)
        {
            effect = new EffectInstance { Id = ctx.Ids.NewId("effect"), Type = BuiltInEffects.Transform };
            clip.Effects.Add(effect);
        }

        effect.Enabled = true;
        return effect;
    }

    private static double Get(EffectInstance effect, string parameter) =>
        effect.Parameters.TryGetValue(parameter, out Parameter? p) ? p.Value : Default(parameter);

    private static void Set(EffectInstance effect, string parameter, double value)
    {
        if (!effect.Parameters.TryGetValue(parameter, out Parameter? p))
        {
            p = new Parameter();
            effect.Parameters[parameter] = p;
        }
        else if (p.Keyframes.Count > 0)
        {
            throw new EditException($"El parámetro «{parameter}» tiene keyframes; el visor solo cambia valores fijos.");
        }

        p.Value = value;
    }

    private static double Default(string parameter) => parameter is P.Scale or P.Opacity ? 1 : 0;
}
