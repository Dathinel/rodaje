using System.Text;
using Rodaje.App.Services.Inspector;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Packages.Content;
using Rodaje.Render.Effects;
using C = Rodaje.Core.Model.BuiltInEffects.ColorParameters;

namespace Rodaje.App.Tests.Inspector;

/// <summary>Cuentagotas de blanco y del calificador, «Guardar como preset» y el costo que muestra la pila.</summary>
public sealed class ColorToolsTests
{
    [Theory]
    [InlineData(0.62, 0.55, 0.50, 0.0, 0.0, 0.0)]
    [InlineData(0.48, 0.50, 0.56, 0.3, 0.2, -0.3)]
    [InlineData(0.30, 0.34, 0.29, -0.5, -0.1, 0.4)]
    public void White_balance_turns_the_sample_gray_after_the_grade(double r, double g, double b, double exposure, double contrast, double saturation)
    {
        (double temperature, double tint) = ColorPickMath.WhiteBalance((r, g, b), exposure, contrast, saturation);

        // Las mismas cuentas que el shader hasta las ganancias: exposición, contraste, saturación, temperatura y tinte.
        double k = Math.Pow(2, exposure);
        double[] c = [.. new[] { r, g, b }.Select(v => ((v * k) - 0.5) * (1 + contrast) + 0.5)];
        double y = (0.2126 * c[0]) + (0.7152 * c[1]) + (0.0722 * c[2]);
        c = [.. c.Select(v => y + ((v - y) * (1 + saturation)))];
        double outR = c[0] * (1 + (0.2 * temperature)), outG = c[1] * (1 - (0.2 * tint)), outB = c[2] * (1 - (0.2 * temperature));

        Assert.Equal(outR, outG, 6);
        Assert.Equal(outR, outB, 6);
    }

    [Fact]
    public void White_balance_of_a_gray_is_neutral_and_it_is_clamped()
    {
        Assert.Equal((0, 0), ColorPickMath.WhiteBalance((0.5, 0.5, 0.5), 0.3, 0.2, 0.1));
        (double temperature, double tint) = ColorPickMath.WhiteBalance((0.9, 0.05, 0.02), 0, 0, 0);
        Assert.InRange(temperature, -1, 1);
        Assert.InRange(tint, -1, 1);
    }

    [Theory]
    [InlineData(1, 0, 0, 0)]
    [InlineData(0, 1, 0, 120)]
    [InlineData(0, 0, 1, 240)]
    [InlineData(1, 1, 0, 60)]
    [InlineData(0.9, 0.6, 0.45, 20)]
    public void The_qualifier_eyedropper_takes_the_hsv_hue(double r, double g, double b, double hue)
    {
        Assert.Equal(hue, ColorPickMath.Qualify((r, g, b)).HueDegrees, 0);
    }

    [Fact]
    public void A_saved_preset_reads_back_as_a_package_filter_with_the_values_at_the_cursor()
    {
        var color = new EffectInstance { Id = "c", Type = BuiltInEffects.Color };
        color.Parameters[C.Exposure] = new Parameter { Keyframes = [new(Time.Zero, 0), new(Time.FromSeconds(2), 1)] };
        color.Parameters[C.Saturation] = new Parameter { Value = -0.25 };
        color.Options[BuiltInEffects.CommonOptions.Source] = "otro@1.0.0/look";
        var grain = new EffectInstance { Id = "g", Type = BuiltInEffects.Grain };
        grain.Options[BuiltInEffects.RegionOptions.Region] = "inside";
        var transform = new EffectInstance { Id = "t", Type = BuiltInEffects.Transform };
        var mask = new EffectInstance { Id = "m", Type = BuiltInEffects.Mask };
        var off = new EffectInstance { Id = "o", Type = BuiltInEffects.Glow, Enabled = false };

        string json = FilterPresetFile.ToJson("propios.mi-look", "Mi look", [transform, color, grain, mask, off], Time.FromSeconds(1));
        FilterPreset preset = ContentReaders.ReadFilter(new MemoryStream(Encoding.UTF8.GetBytes(json)));

        Assert.Equal("Mi look", preset.Name);
        Assert.Equal([BuiltInEffects.Color, BuiltInEffects.Grain], preset.Effects.Select(e => e.Type));
        Assert.Equal(0.5, preset.Effects[0].Parameters[C.Exposure], 6);
        Assert.Equal(-0.25, preset.Effects[0].Parameters[C.Saturation], 6);
        Assert.False(preset.Effects[0].Options.ContainsKey(BuiltInEffects.CommonOptions.Source));
        Assert.Equal("inside", preset.Effects[1].Options[BuiltInEffects.RegionOptions.Region]);

        List<EffectInstance> back = FilterPresetFile.ToInstances(preset);
        Assert.Equal(0.5, back[0].Parameters[C.Exposure].Value, 6);
    }

    [Fact]
    public void The_stack_cost_grows_with_resolution_and_marks_the_heavy_effects()
    {
        EffectCostEstimate hd = EffectCost.Estimate(BuiltInEffects.Denoise, _ => null, 1920, 1080);
        EffectCostEstimate uhd = EffectCost.Estimate(BuiltInEffects.Denoise, _ => null, 3840, 2160);
        Assert.Equal(4 * hd.Milliseconds, uhd.Milliseconds, 6);
        Assert.Equal(CostLevel.High, hd.Level);
        Assert.Equal(CostLevel.Low, EffectCost.Estimate(BuiltInEffects.Color, _ => null, 1920, 1080).Level);
        Assert.Equal(CostLevel.High, EffectCost.Estimate(BuiltInEffects.Blur, o => o == BuiltInEffects.BlurOptions.Kind ? "radial" : null, 1920, 1080).Level);
        Assert.Equal(CostLevel.Low, EffectCost.Estimate(BuiltInEffects.Blur, _ => null, 1920, 1080).Level);
    }
}
