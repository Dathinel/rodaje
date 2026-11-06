using System.Text.RegularExpressions;
using Rodaje.App.Services.Settings;

namespace Rodaje.App.Tests.Settings;

/// <summary>Los grises de los controles de WinUI (ControlColors.xaml) se derivan del tema por niveles (D-057).</summary>
public sealed class ControlTintTests
{
    [Fact]
    public void ConGrafito_CadaGrisVuelveASerElDeFabrica()
    {
        foreach (uint gray in ControlTint.StockGrays)
        {
            Assert.Equal(0xFF000000 | gray, ControlTint.Derive(0xFF000000 | gray, Themes.Graphite));
        }

        // El alfa se conserva y lo que no es un gris de fábrica no se toca aquí.
        Assert.Equal(0x80ECECECu, ControlTint.Derive(0x80ECECEC, Themes.Graphite));
        Assert.Null(ControlTint.Derive(0xFFE8833A, Themes.Graphite));
        Assert.Null(ControlTint.Derive(0x00000000, Themes.Graphite));
        Assert.Null(ControlTint.Derive(0xFFE5484D, Themes.Graphite));
    }

    [Fact]
    public void ConClaro_CadaGrisEsElInvertido()
    {
        foreach (uint gray in ControlTint.StockGrays)
        {
            Assert.Equal(Personalization.Invert(0xFF000000 | gray), ControlTint.Derive(0xFF000000 | gray, Themes.Light));
        }

        Assert.Equal(Personalization.White, ControlTint.TextPole(Themes.Graphite));
        Assert.Equal(0xFF000000u, ControlTint.TextPole(Themes.Light));
    }

    [Fact]
    public void ConPanelesAzulados_LosControlesTomanElAzul()
    {
        ThemeDefinition blue = Themes.Graphite.Copy("Azul");
        blue.Set("PanelBrush", 0xFF1A2A3A);
        blue.Set("ControlBrush", 0xFF28384A);
        blue.Set("ControlBorderBrush", 0xFF6F84A0);

        Assert.Equal(0xFF28384Au, ControlTint.Derive(0xFF2C2C2C, blue));
        Assert.Equal(0xFF6F84A0u, ControlTint.Derive(0xFF737373, blue));
        // Los niveles intermedios (fondo desactivado, borde de un menú) quedan entre sus dos claves: ya no son grises.
        uint disabled = ControlTint.Derive(0xFF232323, blue)!.Value;
        Assert.False(Personalization.IsAchromatic(disabled));
        Assert.InRange(Personalization.Blue(disabled), 0x3A, 0x4A);
        uint separator = ControlTint.Derive(0xFF5C5C5C, blue)!.Value;
        Assert.False(Personalization.IsAchromatic(separator));
        Assert.InRange(Personalization.Blue(separator), 0x3A, 0xA0);
        // El texto de fábrica no cambió: sigue claro, y su intensificado es el blanco.
        Assert.Equal(0xFFECECECu, ControlTint.Derive(0xFFECECEC, blue));
        Assert.Equal(Personalization.White, ControlTint.Derive(0xFFFFFFFF, blue));
    }

    [Fact]
    public void TodosLosGrisesDeControlColorsSeDerivan()
    {
        string xaml = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Rodaje.App", "Theme", "ControlColors.xaml"));
        var grays = new SortedSet<uint>();
        foreach (Match match in Regex.Matches(xaml, "Color=\"#([0-9A-Fa-f]{6,8})\""))
        {
            Assert.True(Personalization.TryParseColor(match.Groups[1].Value, out uint argb));
            if (Personalization.IsAchromatic(argb) && (argb & 0x00FFFFFF) != 0)
            {
                grays.Add(argb & 0x00FFFFFF);
            }
        }

        Assert.NotEmpty(grays);
        Assert.Empty(grays.Except(ControlTint.StockGrays).Select(g => $"#{g:X6}"));
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
}
