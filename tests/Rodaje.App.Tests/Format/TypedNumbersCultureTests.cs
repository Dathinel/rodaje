using System.Globalization;
using Rodaje.App.Services.Audio;
using Rodaje.App.Services.Inspector;

namespace Rodaje.App.Tests.Format;

/// <summary>
/// Los números que se escriben a mano (velocidad, duración, sonoridad) se leen igual con cualquier cultura del hilo: «1,5» y
/// «1.5» valen 1,5, el signo menos tipográfico vale, y lo que no se entiende (cifras nativas, separador de miles con espacio
/// fino, vacío) se rechaza en vez de leerse mal. Revisado en la tarea 228.
/// </summary>
public class TypedNumbersCultureTests
{
    public static TheoryData<string> Cultures => ["", "es-ES", "en-US", "de-DE", "fr-FR", "ar-SA", "fa-IR"];

    [Theory]
    [MemberData(nameof(Cultures))]
    public void LaVelocidadSeLeeIgualEnCualquierCultura(string culture) => WithCulture(culture, () =>
    {
        Assert.Equal(1.5, SpeedMath.ParseRate("1,5")?.ToDouble());
        Assert.Equal(1.5, SpeedMath.ParseRate("1.5")?.ToDouble());
        Assert.Equal(1.5, SpeedMath.ParseRate("  1,5×  ")?.ToDouble());
        Assert.Equal(1.5, SpeedMath.ParseRate("150 %")?.ToDouble());
        Assert.Null(SpeedMath.ParseRate("−3"));
        Assert.Null(SpeedMath.ParseRate("1 000"));
        Assert.Null(SpeedMath.ParseRate("۱٫۵"));
        Assert.Null(SpeedMath.ParseRate(""));
        Assert.Null(SpeedMath.ParseRate("   "));
    });

    [Theory]
    [MemberData(nameof(Cultures))]
    public void LaDuracionSeLeeIgualEnCualquierCultura(string culture) => WithCulture(culture, () =>
    {
        Assert.Equal(1.5, SpeedMath.ParseDuration("1,5")?.ToSeconds());
        Assert.Equal(1.5, SpeedMath.ParseDuration("1.5 s")?.ToSeconds());
        Assert.Equal(65.3, SpeedMath.ParseDuration("1:05,3")!.Value.ToSeconds(), 6);
        Assert.Null(SpeedMath.ParseDuration("−3"));
        Assert.Null(SpeedMath.ParseDuration("1 000"));
        Assert.Null(SpeedMath.ParseDuration("١٫٥"));
        Assert.Null(SpeedMath.ParseDuration(""));
        Assert.Null(SpeedMath.ParseDuration("  "));
    });

    [Theory]
    [MemberData(nameof(Cultures))]
    public void LaSonoridadSeLeeIgualEnCualquierCultura(string culture) => WithCulture(culture, () =>
    {
        Assert.Equal(-18.5, LoudnessNormalizer.ParseTarget("−18,5"));
        Assert.Equal(-18.5, LoudnessNormalizer.ParseTarget("-18.5 LUFS"));
        Assert.Equal(-3, LoudnessNormalizer.ParseTarget(" −3 "));
        Assert.Null(LoudnessNormalizer.ParseTarget("1,5"));
        Assert.Null(LoudnessNormalizer.ParseTarget("-1 000"));
        Assert.Null(LoudnessNormalizer.ParseTarget("-۱۸"));
        Assert.Null(LoudnessNormalizer.ParseTarget(""));
        Assert.Null(LoudnessNormalizer.ParseTarget(null));
        Assert.Equal("−18,5", LoudnessNormalizer.Number(-18.5));
    });

    private static void WithCulture(string name, Action action)
    {
        CultureInfo culture = CultureInfo.CurrentCulture, ui = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(name);
        try
        {
            action();
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = ui;
        }
    }
}
