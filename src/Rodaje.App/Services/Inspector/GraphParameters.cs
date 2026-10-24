using Rodaje.Core.Effects;
using Rodaje.Core.Model;

namespace Rodaje.App.Services.Inspector;

public sealed record GraphParameter(string ClipId, string EffectId, ParameterSpec Spec, string Label);

/// <summary>Catálogo de parámetros animables presentes, incluidos audio y máscaras e instancias repetidas.</summary>
public static class GraphParameters
{
    public static IReadOnlyList<GraphParameter> For(Clip clip, ParameterSpec.Context context)
    {
        ArgumentNullException.ThrowIfNull(clip);
        var result = new List<GraphParameter>();
        for (int i = 0; i < clip.Effects.Count; i++)
        {
            EffectInstance effect = clip.Effects[i];
            foreach ((string name, Parameter parameter) in effect.Parameters)
            {
                ParameterDefinition? definition = EffectRegistry.Find(effect.Type)?.FindParameter(name);
                if (definition is not { Animatable: true })
                {
                    continue;
                }

                ParameterSpec spec = EffectUi.Find(effect.Type, name) is not null
                    ? ParameterSpec.For(effect.Type, name, context)
                    : new ParameterSpec(effect.Type, name, name, "Cambiar " + name, definition.Minimum, definition.Maximum,
                        0.01, 1, parameter.Value, 3, "", true)
                    { ModelMinimum = definition.Minimum, ModelMaximum = definition.Maximum };
                result.Add(new GraphParameter(clip.Id, effect.Id, spec, $"{i + 1}. {EffectUi.Name(effect.Type)} · {spec.Label}"));
            }
        }

        return result;
    }
}
