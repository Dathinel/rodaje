using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Playback.Composition;

namespace Rodaje.Render.Tests.Support;

/// <summary>Efectos evaluados como los arma el motor: valores en el orden del registro, con los que faltan por defecto.</summary>
internal static class Fx
{
    public static LayerEffect Make(string type, ResourceKey? resource = null, params (string Name, double Value)[] values)
    {
        EffectDefinition definition = EffectRegistry.Find(type) ?? throw new ArgumentException(type);
        var slots = new EffectValues();
        foreach (ParameterDefinition parameter in definition.Parameters)
        {
            slots[parameter.Slot] = (float)parameter.DefaultValue;
        }

        foreach ((string name, double value) in values)
        {
            slots[(definition.FindParameter(name) ?? throw new ArgumentException(name)).Slot] = (float)value;
        }

        return new LayerEffect(definition.Code, slots, resource);
    }

    public static LayerEffect Color(params (string Name, double Value)[] values) => Make(BuiltInEffects.Color, null, values);

    public static LayerEffect ColorWithLut(string lut, double intensity) =>
        Make(BuiltInEffects.Color, new ResourceKey(lut), (BuiltInEffects.ColorParameters.LutIntensity, intensity));

    public static LayerEffect Vignette(double amount, double size, double softness) => Make(BuiltInEffects.Vignette, null,
        (BuiltInEffects.VignetteParameters.Amount, amount), (BuiltInEffects.VignetteParameters.Size, size),
        (BuiltInEffects.VignetteParameters.Softness, softness));

    public static LayerEffect Blur(double radius) => Make(BuiltInEffects.Blur, null, (BuiltInEffects.BlurParameters.Radius, radius));

    public static LayerEffect Sharpen(double amount, double radius) => Make(BuiltInEffects.Sharpen, null,
        (BuiltInEffects.SharpenParameters.Amount, amount), (BuiltInEffects.SharpenParameters.Radius, radius));

    public static EffectValues Transition(string type, params (string Name, double Value)[] values) => Make(type, null, values).Values;

    /// <summary>Elige una opción como lo hace el motor: su índice en el hueco que sigue a los parámetros.</summary>
    public static LayerEffect Choose(this LayerEffect effect, string option, string value)
    {
        EffectDefinition definition = EffectRegistry.Find(effect.Code)!;
        int slot = definition.Parameters.Length;
        foreach (OptionDefinition candidate in definition.Options.Where(o => o.Kind == OptionKind.Choice))
        {
            if (candidate.Name == option)
            {
                EffectValues values = effect.Values;
                values[slot] = candidate.Choices.IndexOf(value);
                return effect with { Values = values };
            }

            slot++;
        }

        throw new ArgumentException(option);
    }

    /// <summary>Los segundos del origen que el motor pasa a los efectos que cambian con el tiempo (el grano).</summary>
    public static LayerEffect At(this LayerEffect effect, double seconds)
    {
        EffectValues values = effect.Values;
        values[EffectRegistry.Find(effect.Code)!.TimeSlot] = (float)seconds;
        return effect with { Values = values };
    }
}

/// <summary>LUT ya cargadas; una referencia que no está devuelve <c>false</c>, como un paquete que falta.</summary>
internal sealed class FakeResources : IEffectResources
{
    private readonly Dictionary<string, LutData> _luts = [];

    public event EventHandler? Changed;

    public int Requests { get; private set; }

    public FakeResources Add(string reference, LutData lut)
    {
        _luts[reference] = lut;
        Changed?.Invoke(this, EventArgs.Empty);
        return this;
    }

    public bool TryGetLut(ResourceKey key, out LutData? lut)
    {
        Requests++;
        return _luts.TryGetValue(key.Reference, out lut);
    }
}
