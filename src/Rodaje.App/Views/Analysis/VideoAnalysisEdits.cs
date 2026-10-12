using Rodaje.Analysis.Video;
using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using C = Rodaje.Core.Model.BuiltInEffects.ColorParameters;
using S = Rodaje.Core.Model.BuiltInEffects;

namespace Rodaje.App.Views.Analysis;

/// <summary>Las ediciones de «Estabilizar», «Color automático» y «Balance de blancos automático»: cada una, un paso del historial.</summary>
internal static class VideoAnalysisEdits
{
    /// <summary>
    /// Pone el análisis, la intensidad y el recorte en el primer «Estabilizar» del clip (lo crea si no hay, al principio
    /// de la pila: la corrección va antes que todo).
    /// </summary>
    public static SequenceEditCommand Stabilize(string sequenceId, string clipId, string analysis, double intensity, bool autoCrop) =>
        new("Estabilizar", sequenceId, ctx =>
        {
            List<string> created = [];
            Clip clip = EditableClip(ctx, clipId);
            EffectInstance? effect = clip.Effects.Find(e => e.Type == S.Stabilize && e.Enabled) ?? clip.Effects.Find(e => e.Type == S.Stabilize);
            if (effect is null)
            {
                string id = EffectEdits.AddEffect(ctx, clipId, S.Stabilize, 0);
                created.Add(id);
                effect = clip.Effects.Find(e => e.Id == id)!;
            }

            effect.Enabled = true;
            Set(effect, S.StabilizeParameters.Intensity, intensity);
            effect.Options[S.StabilizeOptions.Crop] = autoCrop ? "auto" : "off";
            effect.Options[S.StabilizeOptions.Analysis] = analysis;
            return created;
        });

    /// <summary>
    /// Lleva los valores al primer efecto de color activo del clip (lo crea si no hay). <paramref name="levels"/> también
    /// fija exposición y contraste; sin él, el balance fija temperatura, tinte y su compensación de exposición. Los
    /// parámetros que toca pierden sus keyframes.
    /// </summary>
    public static SequenceEditCommand Color(string sequenceId, string clipId, ColorSettings settings, bool levels) =>
        new(levels ? "Color automático" : "Balance de blancos automático", sequenceId, ctx =>
        {
            List<string> created = [];
            Clip clip = EditableClip(ctx, clipId);
            EffectInstance? effect = clip.Effects.Find(e => e.Type == S.Color && e.Enabled);
            if (effect is null)
            {
                string id = EffectEdits.AddEffect(ctx, clipId, S.Color);
                created.Add(id);
                effect = clip.Effects.Find(e => e.Id == id)!;
            }

            Set(effect, C.Temperature, settings.Temperature);
            Set(effect, C.Tint, settings.Tint);
            Set(effect, C.Exposure, settings.Exposure);
            if (levels)
            {
                Set(effect, C.Contrast, settings.Contrast);
            }

            return created;
        });

    private static void Set(EffectInstance effect, string name, double value)
    {
        ParameterDefinition? definition = EffectRegistry.Find(effect.Type)?.FindParameter(name);
        double clamped = definition?.Clamp(value) ?? value;
        effect.Parameters[name] = new Parameter { Value = double.IsFinite(clamped) ? clamped : definition?.DefaultValue ?? 0 };
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
