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

    [Theory]
    [MemberData(nameof(Cultures))]
    public void LosCuadrosNumericosLeenComaYPuntoEnCualquierCultura(string culture) => WithCulture(culture, () =>
    {
        Assert.Equal(29.97, TypedNumber.Parse("29,97"));
        Assert.Equal(29.97, TypedNumber.Parse("29.97"));
        Assert.Equal(0.5, TypedNumber.Parse(" 0.5 "));
        Assert.Equal(1, TypedNumber.Parse("1.000"));
        Assert.Equal(-3, TypedNumber.Parse("−3"));
        Assert.Equal(-2.5, TypedNumber.Parse("-2,5"));
        Assert.Null(TypedNumber.Parse("1.000,5"));
        Assert.Null(TypedNumber.Parse("1 000"));
        Assert.Null(TypedNumber.Parse("٢٩٫٩٧"));
        Assert.Null(TypedNumber.Parse("1e999"));
        Assert.Null(TypedNumber.Parse(""));
        Assert.Null(TypedNumber.Parse(null));
        Assert.Equal("29,97", TypedNumber.Format(29.97, 2));
        Assert.Equal("0,13", TypedNumber.Format(0.125, 2));
        Assert.Equal("0,00", TypedNumber.Format(-0.001, 2));
        Assert.Equal("1920", TypedNumber.Format(1920, 0));
        Assert.Equal(TypedNumber.Parse(TypedNumber.Format(-12.34, 2)), -12.34);
    });

    /// <summary>
    /// El <c>DecimalFormatter</c> de WinRT sigue el idioma de Windows, no la app: en es-CO rechaza «0.5» y lee «1.000» como
    /// mil; en en-US rechaza «1,5» y en ar-SA muestra cifras arábigas. Los cuadros usan <c>TypedNumberFormatter</c>.
    /// </summary>
    [Fact]
    public void NingunCuadroNumericoUsaElFormatoDeWindows()
    {
        string root = Path.Combine(RepositoryRoot(), "src", "Rodaje.App");
        var found = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Contains("DecimalFormatter", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(root, f))
            .ToList();
        Assert.True(File.Exists(Path.Combine(root, "Views", "Inspector", "TypedNumberFormatter.cs")), "la vigilancia no lee la app");
        Assert.Empty(found);
    }

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Rodaje.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("No encuentro Rodaje.slnx subiendo desde las pruebas.");
    }

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
