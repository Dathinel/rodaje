using Rodaje.Core.Effects;
using Rodaje.Core.Model;

namespace Rodaje.Core.Tests.Model;

public class EffectRegistryTests
{
    [Fact]
    public void CodesAndTypes_AreUnique()
    {
        Assert.Equal(EffectRegistry.All.Length, EffectRegistry.All.Select(d => d.Code).Distinct().Count());
        Assert.Equal(EffectRegistry.All.Length, EffectRegistry.All.Select(d => d.Type).Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>Los códigos viajan al render y a los shaders: cambiarlos rompe la tabla de la GPU.</summary>
    [Fact]
    public void Codes_AreStable()
    {
        Assert.Equal(
            [
                (BuiltInEffects.Transform, 1), (BuiltInEffects.Color, 2), (BuiltInEffects.Vignette, 3), (BuiltInEffects.Blur, 4),
                (BuiltInEffects.Sharpen, 5), (BuiltInEffects.Blend, 6), (BuiltInEffects.Audio, 7), (BuiltInEffects.Speed, 8),
                (BuiltInEffects.Stabilize, 9), (BuiltInEffects.Mask, 10), (BuiltInEffects.Crop, 11),
                (BuiltInEffects.ChromaKey, 12), (BuiltInEffects.ColorWheels, 13), (BuiltInEffects.Curves, 14), (BuiltInEffects.Equalizer, 20), (BuiltInEffects.Compressor, 21),
                (BuiltInTransitions.Crossfade, 101), (BuiltInTransitions.Dip, 102),
                (BuiltInTransitions.Wipe, 103), (BuiltInTransitions.Push, 104), (BuiltInTransitions.ZoomDissolve, 105),
                (BuiltInTransitions.BlurDissolve, 106), (BuiltInTransitions.Preset, 107),
            ],
            EffectRegistry.All.Select(d => (d.Type, d.Code)));
    }

    [Fact]
    public void Definitions_AreConsistent()
    {
        foreach (EffectDefinition definition in EffectRegistry.All)
        {
            Assert.InRange(definition.Parameters.Length, 0, EffectDefinition.MaxParameters);
            Assert.Equal(definition.Parameters.Length, definition.Parameters.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(definition.Options.Length, definition.Options.Select(o => o.Name).Distinct(StringComparer.Ordinal).Count());
            Assert.DoesNotContain(definition.Options, o => o.Name == BuiltInEffects.CommonOptions.Source);
            Assert.Equal(definition.IsTransition, definition.Stage == EffectStage.Transition);
            for (int i = 0; i < definition.Parameters.Length; i++)
            {
                ParameterDefinition p = definition.Parameters[i];
                Assert.Equal(i, p.Slot);
                Assert.InRange(p.DefaultValue, p.Minimum, p.Maximum);
                Assert.True(p.Minimum < p.Maximum, $"{definition.Type}.{p.Name}");
                Assert.False(definition.IsTransition && p.Animatable, $"{definition.Type}.{p.Name}: una transición no tiene keyframes.");
            }

            foreach (OptionDefinition option in definition.Options)
            {
                Assert.True(option.Kind == OptionKind.PackageReference || option.Accepts(option.DefaultValue), $"{definition.Type}.{option.Name}");
            }
        }
    }

    [Fact]
    public void Transitions_AreTheSixOwnPlusPresets()
    {
        Assert.Equal(
            [
                BuiltInTransitions.Crossfade, BuiltInTransitions.Dip, BuiltInTransitions.Wipe, BuiltInTransitions.Push,
                BuiltInTransitions.ZoomDissolve, BuiltInTransitions.BlurDissolve, BuiltInTransitions.Preset,
            ],
            EffectRegistry.Transitions.Select(d => d.Type));
        Assert.All(EffectRegistry.Transitions, d => Assert.Equal(EffectTarget.Transition, d.Target));
        Assert.Equal(Rodaje.Core.Timing.Time.FromSeconds(0.5), EffectRegistry.DefaultTransitionDuration);
    }

    [Fact]
    public void Blend_HasThirteenModes()
    {
        OptionDefinition mode = EffectRegistry.Find(BuiltInEffects.Blend)!.FindOption(BuiltInEffects.BlendOptions.Mode)!;

        Assert.Equal(13, EffectRegistry.BlendModes.Length);
        Assert.Equal(EffectRegistry.BlendModes.AsEnumerable(), mode.Choices.AsEnumerable());
        Assert.Equal("normal", mode.DefaultValue);
        Assert.True(mode.Accepts("softLight"));
        Assert.False(mode.Accepts("SoftLight"));
        Assert.Equal("normal", EffectRegistry.DefaultOption(BuiltInEffects.Blend, BuiltInEffects.BlendOptions.Mode));
        Assert.Equal("", EffectRegistry.DefaultOption(BuiltInEffects.Color, BuiltInEffects.ColorOptions.Lut));
        Assert.Equal("", EffectRegistry.DefaultOption("otro.tipo", "x"));
        Assert.True(EffectRegistry.Find(BuiltInEffects.Color)!.FindOption(BuiltInEffects.ColorOptions.Lut)!.Accepts("lo-que-sea@1.0.0/x"));
        Assert.True(EffectRegistry.Find(BuiltInEffects.Color)!.FindOption(BuiltInEffects.ColorOptions.Lut)!.Accepts("file:C:/luts/x.cube"));
        Assert.False(EffectRegistry.Find(BuiltInTransitions.Preset)!.FindOption(BuiltInTransitions.PresetOptions.Preset)!.Accepts("file:x.json"));
    }

    [Fact]
    public void Lookups_ByTypeAndCode()
    {
        EffectDefinition color = EffectRegistry.Find(BuiltInEffects.Color)!;

        Assert.Same(color, EffectRegistry.Find(color.Code));
        Assert.Null(EffectRegistry.Find(999));
        Assert.Null(EffectRegistry.Find("rodaje.volume"));
        Assert.True(EffectRegistry.IsKnown(BuiltInTransitions.Wipe));
        Assert.False(EffectRegistry.IsKnown("otro.programa.brillo"));
        Assert.Equal(1, color.FindParameter(BuiltInEffects.ColorParameters.Contrast)!.Slot);
        Assert.Null(color.FindParameter("nada"));
        Assert.Null(color.FindOption("nada"));
        Assert.Equal(1, EffectRegistry.DefaultValue(BuiltInEffects.Color, BuiltInEffects.ColorParameters.LutIntensity));
        Assert.Equal(0, EffectRegistry.DefaultValue("otro.tipo", "x"));
        Assert.Equal("rodaje.color (2)", color.ToString());
    }

    [Fact]
    public void Transform_DefaultsMatchTheOldHelper()
    {
        foreach (ParameterDefinition p in EffectRegistry.Find(BuiltInEffects.Transform)!.Parameters)
        {
            Assert.Equal(BuiltInEffects.TransformParameters.DefaultValue(p.Name), p.DefaultValue);
        }
    }

    [Fact]
    public void Parameter_ClampsAndReplacesNaN()
    {
        ParameterDefinition opacity = EffectRegistry.Find(BuiltInEffects.Transform)!.FindParameter(BuiltInEffects.TransformParameters.Opacity)!;

        Assert.Equal(1, opacity.Clamp(3));
        Assert.Equal(0, opacity.Clamp(-1));
        Assert.Equal(1, opacity.Clamp(double.NaN));
        Assert.Equal(EffectRegistry.SilenceDb, EffectRegistry.Find(BuiltInEffects.Audio)!.FindParameter(BuiltInEffects.AudioParameters.GainDb)!.Minimum);
    }

    [Fact]
    public void Definition_ChecksItsArguments()
    {
        ParameterDefinition[] tooMany = [.. Enumerable.Range(0, EffectDefinition.MaxParameters + 1).Select(i => new ParameterDefinition($"p{i}", 0, 0, 1))];

        Assert.Throws<ArgumentOutOfRangeException>(() => new EffectDefinition("x", 1, EffectTarget.Video, EffectStage.PerPixel, [.. tooMany]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EffectDefinition("x", 0, EffectTarget.Video, EffectStage.PerPixel));
        Assert.Throws<ArgumentException>(() => new EffectDefinition(" ", 1, EffectTarget.Video, EffectStage.PerPixel));
        Assert.Empty(new EffectDefinition("x", 1, EffectTarget.Video, EffectStage.PerPixel).Options);
    }
}
