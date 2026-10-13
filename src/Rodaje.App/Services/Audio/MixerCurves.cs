using Rodaje.Audio.Processing;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using G = Rodaje.Core.Model.BuiltInEffects.GateParameters;
using Q = Rodaje.Core.Model.BuiltInEffects.EqualizerParameters;

namespace Rodaje.App.Services.Audio;

/// <summary>Las curvas que dibuja el mezclador, con las mismas cuentas que suenan (las de <c>Rodaje.Audio</c>).</summary>
public static class MixerCurves
{
    private const double Rate = 48_000;

    /// <summary>La respuesta en dB del ecualizador paramétrico o gráfico en cada frecuencia (Hz); 0 en otro tipo.</summary>
    public static Func<double, double> Response(EffectInstance effect)
    {
        ArgumentNullException.ThrowIfNull(effect);
        double V(string name) => Value(effect, name);
        switch (effect.Type)
        {
            case BuiltInEffects.Equalizer:
            {
                var eq = new EqualizerSettings(V(Q.LowCut), V(Q.LowGain), V(Q.LowFrequency), V(Q.MidGain), V(Q.MidFrequency), V(Q.MidQ),
                    V(Q.PresenceGain), V(Q.PresenceFrequency), V(Q.PresenceQ), V(Q.HighGain), V(Q.HighFrequency));
                return hz => eq.MagnitudeDb(Rate, hz);
            }

            case BuiltInEffects.GraphicEqualizer:
            {
                IReadOnlyList<string> b = BuiltInEffects.GraphicEqualizerParameters.Bands;
                var graphic = new GraphicEqualizerSettings(V(b[0]), V(b[1]), V(b[2]), V(b[3]), V(b[4]), V(b[5]), V(b[6]), V(b[7]), V(b[8]), V(b[9]))
                {
                    OutputDb = V(BuiltInEffects.GraphicEqualizerParameters.Output),
                };
                return hz => graphic.MagnitudeDb(Rate, hz);
            }

            default:
                return _ => 0;
        }
    }

    public static GateSettings Gate(EffectInstance effect)
    {
        ArgumentNullException.ThrowIfNull(effect);
        return new GateSettings(Value(effect, G.Threshold), Value(effect, G.Range), Value(effect, G.Ratio), Value(effect, G.Attack),
            Value(effect, G.Hold), Value(effect, G.Release)) { HysteresisDb = Value(effect, G.Hysteresis) };
    }

    /// <summary>El valor fijo de un parámetro (el primer keyframe si tiene), dentro de sus límites; si falta, el de fábrica.</summary>
    public static double Value(EffectInstance effect, string name)
    {
        ArgumentNullException.ThrowIfNull(effect);
        ParameterDefinition? definition = EffectRegistry.Find(effect.Type)?.FindParameter(name);
        double fallback = definition?.DefaultValue ?? 0;
        if (!effect.Parameters.TryGetValue(name, out Parameter? p))
        {
            return fallback;
        }

        double value = p.Keyframes.Count > 0 ? p.Keyframes[0].Value : p.Value;
        return definition?.Clamp(value) ?? value;
    }
}
