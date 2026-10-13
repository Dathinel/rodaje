using Rodaje.Core.Model;
using M = Rodaje.Core.Model.BuiltInEffects.MaskParameters;
using T = Rodaje.Core.Model.BuiltInEffects.TransformParameters;

namespace Rodaje.Core.Effects;

/// <summary>
/// Hasta la v1.0.0 la posición, el ancla y el giro de la transformación y el centro y el giro de la máscara no tenían
/// límite en el modelo; hoy lo tienen (<see cref="EffectRegistry.MaxOffset"/>, <see cref="EffectRegistry.MaxRotation"/>,
/// <see cref="EffectRegistry.MaxMaskOffset"/>). Un proyecto guardado entonces con un valor más allá se abre igual: al
/// leerlo, esos valores (también sus keyframes y las curvas x, y y rotación de las animaciones de los textos) se acotan
/// al límite. Lo que entra por edición lo sigue rechazando el validador.
/// </summary>
public static class ParameterLimits
{
    /// <summary>Los parámetros que no tenían límite en la v1.0.0, por tipo de efecto.</summary>
    private static readonly (string Type, string[] Names)[] FormerlyUnbounded =
    [
        (BuiltInEffects.Transform, [T.X, T.Y, T.Rotation, T.AnchorX, T.AnchorY]),
        (BuiltInEffects.Mask, [M.X, M.Y, M.Rotation]),
    ];

    /// <summary>Acota en el sitio los valores finitos fuera de rango de esos parámetros; devuelve cuántos cambió.</summary>
    /// <remarks>
    /// No da por hecho que <c>FindNullItems</c> ya rechazó las listas o los elementos nulos: una lista, un efecto, un
    /// parámetro o sus keyframes nulos se saltan, y lo que no se puede recorrer lo rechaza después el validador.
    /// </remarks>
    public static int Normalize(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        int changed = 0;
        foreach (Sequence? sequence in project.Sequences ?? [])
        {
            if (sequence is null)
            {
                continue;
            }

            changed += Normalize(sequence.MasterEffects);
            foreach (AudioBus? bus in sequence.AudioBuses ?? [])
            {
                changed += Normalize(bus?.Effects);
            }

            foreach (Track? track in (sequence.VideoTracks ?? []).Concat(sequence.AudioTracks ?? []))
            {
                if (track is null)
                {
                    continue;
                }

                changed += Normalize(track.Effects);
                foreach (Clip? clip in track.Clips ?? [])
                {
                    if (clip is null)
                    {
                        continue;
                    }

                    changed += Normalize(clip.Effects);
                    if (clip.Generator?.Style is { } style)
                    {
                        changed += Normalize(style.In) + Normalize(style.Out);
                    }
                }
            }
        }

        return changed;
    }

    private static int Normalize(List<EffectInstance>? effects)
    {
        int changed = 0;
        foreach (EffectInstance? effect in effects ?? [])
        {
            if (effect?.Parameters is not { } parameters)
            {
                continue;
            }

            foreach ((string type, string[] names) in FormerlyUnbounded)
            {
                if (effect.Type != type || EffectRegistry.Find(type) is not { } definition)
                {
                    continue;
                }

                foreach (string name in names)
                {
                    if (parameters.GetValueOrDefault(name) is { } parameter)
                    {
                        changed += Normalize(parameter, definition.FindParameter(name)!);
                    }
                }
            }
        }

        return changed;
    }

    private static int Normalize(Parameter parameter, ParameterDefinition limits)
    {
        int changed = 0;
        if (Outside(parameter.Value, limits))
        {
            parameter.Value = Math.Clamp(parameter.Value, limits.Minimum, limits.Maximum);
            changed++;
        }

        if (parameter.Keyframes is not { } keyframes)
        {
            return changed;
        }

        for (int i = 0; i < keyframes.Count; i++)
        {
            if (keyframes[i] is { } key && Outside(key.Value, limits))
            {
                keyframes[i] = key with { Value = Math.Clamp(key.Value, limits.Minimum, limits.Maximum) };
                changed++;
            }
        }

        return changed;
    }

    /// <summary>Las curvas x, y y rotación de una animación de texto usan los límites de la transformación.</summary>
    private static int Normalize(StyleAnimation? animation)
    {
        if (animation?.Curves is null)
        {
            return 0;
        }

        EffectDefinition transform = EffectRegistry.Find(BuiltInEffects.Transform)!;
        int changed = 0;
        foreach ((string name, List<StyleKey>? keys) in animation.Curves)
        {
            if (name is not (T.X or T.Y or T.Rotation) || keys is null || transform.FindParameter(name) is not { } limits)
            {
                continue;
            }

            for (int i = 0; i < keys.Count; i++)
            {
                if (keys[i] is { } key && Outside(key.Value, limits))
                {
                    keys[i] = key with { Value = Math.Clamp(key.Value, limits.Minimum, limits.Maximum) };
                    changed++;
                }
            }
        }

        return changed;
    }

    /// <summary>Solo lo finito fuera del rango: NaN e infinitos no llegan de un JSON y, si llegan, los rechaza el validador.</summary>
    private static bool Outside(double value, ParameterDefinition limits) =>
        double.IsFinite(value) && (value < limits.Minimum || value > limits.Maximum);
}
