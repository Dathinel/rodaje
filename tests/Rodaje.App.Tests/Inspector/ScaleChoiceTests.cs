using Rodaje.App.Services.Format;
using Rodaje.App.Services.Inspector;
using Rodaje.App.Services.Settings;
using Rodaje.Core.Effects;
using T = Rodaje.Core.Model.BuiltInEffects.TransformParameters;

namespace Rodaje.App.Tests.Inspector;

/// <summary>
/// Tarea 91 («la app se cierra al elegir escala»): la lógica de cada selector de escala de la vista, sin WinUI. Elegir
/// cualquier opción (y los índices que WinUI manda al vaciar o rehacer un desplegable) no lanza ni deja pasar un valor
/// fuera de rango hacia el motor.
/// </summary>
public class ScaleChoiceTests
{
    private static readonly string[] Animated = [T.X, T.Y, T.Scale, T.ScaleX, T.ScaleY, T.Rotation];

    private static readonly double[] Extremes =
    [
        0, -0.0, -1, -1e-12, 1e-12, double.Epsilon, 1e300, -1e300, double.MaxValue, double.MinValue,
        double.NaN, double.PositiveInfinity, double.NegativeInfinity, 400, 10_000,
    ];

    [Fact]
    public void Grafica_ElegirCadaParametro_NoRehaceLaLista_YLoMuestra()
    {
        (IReadOnlyList<string> shown, int first) = GraphParameterChoice.Choose(Animated, null);
        Assert.Equal(0, first);
        for (int index = 0; index < shown.Count; index++)
        {
            // Lo que hace la vista al cambiar la selección: traducir el índice, recordar y refrescar.
            string? picked = ComboChoices.Picked(shown, index);
            Assert.Equal(Animated[index], picked);
            (IReadOnlyList<string> next, int selected) = GraphParameterChoice.Choose(Animated, picked);
            Assert.Equal(index, selected);
            Assert.False(ComboChoices.NeedsRebuild(shown, next), $"Elegir «{picked}» no debe rehacer los elementos del desplegable.");
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-2)]
    [InlineData(6)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    public void Grafica_IndicesFueraDeLaLista_NoEligenNada(int index)
    {
        Assert.Null(ComboChoices.Picked(Animated, index));
        Assert.Null(ComboChoices.Picked(Array.Empty<string>(), index));
    }

    [Fact]
    public void Grafica_SinAnimar_OConElRecordadoQuitado()
    {
        Assert.Equal(-1, GraphParameterChoice.Choose([], T.Scale).Selected);
        Assert.Equal(0, GraphParameterChoice.Choose([T.ScaleX, T.ScaleY], T.Scale).Selected);
        Assert.Equal(1, GraphParameterChoice.Choose([T.ScaleX, T.Scale], T.Scale).Selected);
        Assert.True(ComboChoices.NeedsRebuild([T.Scale], [T.Scale, T.ScaleX]));
        Assert.True(ComboChoices.NeedsRebuild([T.Scale], []));
        Assert.False(ComboChoices.NeedsRebuild([], []));
    }

    [Fact]
    public void Propiedades_EscalaAnchoYAlto_ConValoresExtremos_LleganAlRangoDelModelo()
    {
        foreach (ParameterSpec spec in ParameterSpec.Transform(1920, 1080).Where(s => s.Name is T.Scale or T.ScaleX or T.ScaleY))
        {
            Assert.Equal((1, 400), (spec.Minimum, spec.Maximum));
            AssertSafe(spec);
        }

        ParameterSpec scale = ParameterSpec.Transform(1920, 1080).Single(s => s.Name == T.Scale);
        Assert.Equal(1, scale.ToModel(double.NaN));
        Assert.Equal(100, scale.ToModel(double.PositiveInfinity));
        Assert.Equal(0, scale.ToModel(double.NegativeInfinity));
        Assert.Equal(0, scale.ToModel(-50));
        Assert.Equal(100, scale.ToModel(1e300));
        Assert.Equal(1, scale.ToSlider(0));
        Assert.Equal(400, scale.ToSlider(100));
        Assert.Equal(100, scale.ToSlider(double.NaN));
        Assert.Equal(100, scale.ToBox(double.NaN));
    }

    [Fact]
    public void TodosLosParametrosDelRegistro_ConValoresExtremos_SonSeguros()
    {
        var context = new ParameterSpec.Context(1920, 1080, 8);
        foreach (EffectDefinition definition in EffectRegistry.All)
        {
            foreach (ParameterSpec spec in ParameterSpec.ForEffect(definition.Type, context))
            {
                AssertSafe(spec);
            }
        }
    }

    [Fact]
    public void FormatoDelProyecto_CadaAjusteDeClips_TieneNombre_YUnoFueraDeLaListaEsEncajar()
    {
        Assert.Equal(Enum.GetValues<ClipFit>(), ProjectFormats.FitChoices.Select(f => f.Fit));
        foreach ((ClipFit fit, string label, string _) in ProjectFormats.FitChoices)
        {
            Assert.Equal(fit, ProjectFormats.Normalize(fit));
            Assert.Equal(label, ProjectFormats.FitLabel(fit));
        }

        Assert.Equal(ClipFit.Fit, ProjectFormats.Normalize((ClipFit)42));
        Assert.Equal(ClipFit.Fit, ProjectFormats.Normalize((ClipFit)(-1)));
        Assert.Equal("Encajar", ProjectFormats.FitLabel((ClipFit)42));
    }

    [Fact]
    public void FormatoDelProyecto_UnAjusteGuardadoFueraDeLaLista_SeCorrigeAlLeer()
    {
        var settings = new AppSettings { FormatClipFit = (ClipFit)7, PreviewQuality = (PreviewQuality)9 };
        settings.Normalize();
        Assert.Equal(ClipFit.Fit, settings.FormatClipFit);
        Assert.Equal(PreviewQuality.Auto, settings.PreviewQuality);
    }

    [Theory]
    [InlineData("Auto", 1)]
    [InlineData("Full", 1)]
    [InlineData("Half", 2)]
    [InlineData("Quarter", 4)]
    public void VistaPrevia_CadaCalidad_DivideBien(string tag, int divisor)
    {
        // Lo que hace el clic del menú «Calidad» (Completa, Mitad, Cuarto): el Tag pasa a la calidad y al divisor.
        Assert.True(Enum.TryParse(tag, out PreviewQuality quality));
        var governor = new PreviewQualityGovernor { Quality = quality };
        Assert.Equal(divisor, governor.Divisor);
    }

    private static void AssertSafe(ParameterSpec spec)
    {
        foreach (double value in Extremes)
        {
            double model = spec.ToModel(value);
            Assert.True(double.IsFinite(model), $"{spec.EffectType}/{spec.Name}: ToModel({value}) = {model}");
            Assert.InRange(model, spec.ModelMinimum, spec.ModelMaximum);

            double slider = spec.ToSlider(value);
            Assert.True(double.IsFinite(slider), $"{spec.EffectType}/{spec.Name}: ToSlider({value}) = {slider}");
            Assert.InRange(slider, spec.Minimum, spec.Maximum);

            double box = spec.ToBox(value);
            Assert.True(double.IsFinite(box), $"{spec.EffectType}/{spec.Name}: ToBox({value}) = {box}");
        }
    }
}
