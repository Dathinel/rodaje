using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Playback.Composition;

namespace Rodaje.Engine.Timeline;

/// <summary>Los efectos de imagen de una pila evaluados en un instante, como los recibe el compositor (miniaturas de presets).</summary>
public static class EffectEvaluation
{
    /// <param name="effects">La pila; transformación, recorte, mezcla y estabilizar no entran (no son de imagen).</param>
    /// <param name="sourceTime">Instante en tiempo del origen del clip.</param>
    public static LayerEffect[] Evaluate(IReadOnlyList<EffectInstance> effects, Time sourceTime, IEffectResources? resources = null)
    {
        ArgumentNullException.ThrowIfNull(effects);
        if (EffectPlan.From(effects) is not { EffectCount: > 0 } plan)
        {
            return [];
        }

        var values = new LayerEffect[plan.EffectCount];
        plan.Evaluate(sourceTime, values, resources);
        return values;
    }
}
