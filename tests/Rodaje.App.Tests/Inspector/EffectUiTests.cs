using Rodaje.App.Services.Inspector;
using Rodaje.App.Services.Packages;
using Rodaje.App.Services.Titles;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Packages;

namespace Rodaje.App.Tests.Inspector;

public class EffectUiTests
{
    private static readonly ParameterSpec.Context Hd = new(1920, 1080, 8);

    [Fact]
    public void CadaTipoYParametroDelRegistro_TieneNombreYPresentacion()
    {
        foreach (EffectDefinition definition in EffectRegistry.All)
        {
            Assert.NotEqual(definition.Type, EffectUi.Name(definition.Type));
            Assert.NotEmpty(EffectUi.Description(definition.Type));
            foreach (ParameterDefinition parameter in definition.Parameters)
            {
                Assert.True(EffectUi.Find(definition.Type, parameter.Name) is not null, $"Falta {definition.Type}/{parameter.Name}");
                ParameterSpec spec = ParameterSpec.For(definition.Type, parameter.Name, Hd);
                Assert.True(spec.Minimum < spec.Maximum, $"{definition.Type}/{parameter.Name}");
                Assert.Equal(parameter.DefaultValue, spec.DefaultValue);
                Assert.Equal(parameter.Animatable, spec.Animatable);
                Assert.InRange(spec.ToDisplay(parameter.DefaultValue), spec.Minimum, spec.Maximum);
            }
        }
    }

    [Fact]
    public void LosRangosSalenDelRegistro_LaGananciaLlegaA24dB()
    {
        ParameterSpec gain = ParameterSpec.For(BuiltInEffects.Audio, BuiltInEffects.AudioParameters.GainDb, Hd);
        Assert.Equal((-60, 24), (gain.Minimum, gain.Maximum));
        Assert.Equal("dB", gain.Unit);
        Assert.True(gain.Animatable);

        ParameterSpec exposure = ParameterSpec.For(BuiltInEffects.Color, BuiltInEffects.ColorParameters.Exposure, Hd);
        Assert.Equal((-4, 4), (exposure.Minimum, exposure.Maximum));
        Assert.Equal(2, exposure.Decimals);

        ParameterSpec contrast = ParameterSpec.For(BuiltInEffects.Color, BuiltInEffects.ColorParameters.Contrast, Hd);
        Assert.Equal((-100, 100), (contrast.Minimum, contrast.Maximum));
        Assert.Equal("%", contrast.Unit);
    }

    [Fact]
    public void LoEscritoAManoSeLlevaAlLimiteDelModelo()
    {
        ParameterSpec gain = ParameterSpec.For(BuiltInEffects.Audio, BuiltInEffects.AudioParameters.GainDb, Hd);
        Assert.Equal(24, gain.ToModel(40));
        Assert.Equal(-60, gain.ToModel(-100));

        ParameterSpec radius = ParameterSpec.For(BuiltInEffects.Blur, BuiltInEffects.BlurParameters.Radius, Hd);
        Assert.Equal(100, radius.Maximum);
        Assert.Equal(250, radius.ToModel(250));
    }

    [Fact]
    public void ParametroDesconocido_Lanza()
    {
        Assert.Throws<ArgumentException>(() => ParameterSpec.For("otro.efecto", "x", Hd));
        Assert.Throws<ArgumentException>(() => ParameterSpec.For(BuiltInEffects.Color, "nada", Hd));
        Assert.Empty(ParameterSpec.ForEffect("otro.efecto", Hd));
    }

    [Fact]
    public void LosTreceModosDeMezcla_EnElOrdenDelRegistro()
    {
        Assert.Equal(EffectRegistry.BlendModes, EffectUi.BlendModes.Select(m => m.Mode));
        Assert.Equal(13, EffectUi.BlendModes.Select(m => m.Name).Distinct().Count());
        Assert.Equal("Superponer", EffectUi.BlendModeName("overlay"));
        Assert.Equal("Superponer", EffectUi.ChoiceName(BuiltInEffects.BlendOptions.Mode, "overlay"));
        Assert.Equal(EffectRegistry.PushDirections, EffectUi.PushDirections.Select(d => d.Value));
        Assert.Equal("Hacia arriba", EffectUi.ChoiceName(BuiltInTransitions.PushOptions.Direction, "up"));
    }

    [Fact]
    public void Biblioteca_SinProximamenteNiFase5_YConElTextoDeFabrica()
    {
        foreach (LibraryCategory category in Enum.GetValues<LibraryCategory>())
        {
            foreach (LibraryElement element in LibraryElements.For(category, PackageScan.Empty))
            {
                string text = element.Name + element.Description + element.Source;
                Assert.DoesNotContain("róximamente", text, StringComparison.Ordinal);
                Assert.DoesNotContain("fase 5", text, StringComparison.Ordinal);
                Assert.True(element.IsAvailable);
            }
        }

        Assert.Equal(TitleStyles.BuiltInReference, Assert.Single(LibraryElements.BuiltIn(LibraryCategory.Text)).Reference);
        Assert.Equal(
            [BuiltInEffects.Transform, BuiltInEffects.Vignette, BuiltInEffects.Blur, BuiltInEffects.Sharpen, BuiltInEffects.Blend,
                BuiltInEffects.Crop, BuiltInEffects.ChromaKey],
            LibraryElements.BuiltIn(LibraryCategory.Effects).Select(e => e.Reference));
        Assert.Equal([BuiltInEffects.Color, BuiltInEffects.ColorWheels, BuiltInEffects.Curves],
            LibraryElements.BuiltIn(LibraryCategory.Filters).Select(e => e.Reference));
        Assert.Equal(6, LibraryElements.BuiltIn(LibraryCategory.Transitions).Count);
    }
}
