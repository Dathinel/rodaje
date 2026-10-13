using Rodaje.App.Services.Crashes;

namespace Rodaje.App.Tests.Crashes;

/// <summary>
/// <c>%TEMP%</c> en formato 8.3 (<c>C:\Users\ANAPR~1\…</c>, como en el runner de la CI) y la ruta de un archivo en nombre
/// largo, o al revés (#263): el limpiador debe reconocer las dos formas. Antes solo conocía la de la variable, y una ruta
/// en la otra forma caía en <c>%LOCALAPPDATA%</c> con sus carpetas a la vista (<c>%LOCALAPPDATA%\Temp\Boda Pérez\…</c>).
/// La expansión a nombre largo se inyecta: en Linux no hay <c>GetLongPathNameW</c>.
/// </summary>
public sealed class CrashReportScrubberShortTempTests
{
    private const string ShortTemp = @"C:\Users\ANAPR~1\AppData\Local\Temp";
    private const string LongTemp = @"C:\Users\Ana Pérez\AppData\Local\Temp";

    private static readonly Dictionary<string, string?> ShortEnvironment = new(StringComparer.Ordinal)
    {
        ["TEMP"] = ShortTemp,
        ["TMP"] = ShortTemp,
        ["LOCALAPPDATA"] = @"C:\Users\Ana Pérez\AppData\Local",
        ["APPDATA"] = @"C:\Users\Ana Pérez\AppData\Roaming",
        ["USERPROFILE"] = @"C:\Users\Ana Pérez",
    };

    private static string? ToLong(string path) =>
        path.Replace(@"\ANAPR~1", @"\Ana Pérez", StringComparison.OrdinalIgnoreCase);

    public static TheoryData<string, string> Paths => new()
    {
        // Nombre largo con %TEMP% en 8.3: el caso del defecto.
        { LongTemp + @"\Boda Pérez\clip 1.mp4", @"%TEMP%\…\clip 1.mp4" },
        { LongTemp + @"\rodaje-exportar-3\clip 1.mp4", @"%TEMP%\rodaje-exportar-3\clip 1.mp4" },
        // La forma 8.3 sigue reconocida.
        { ShortTemp + @"\Boda Pérez\clip 1.mp4", @"%TEMP%\…\clip 1.mp4" },
        { ShortTemp + @"\rodaje-exportar-3\clip 1.mp4", @"%TEMP%\rodaje-exportar-3\clip 1.mp4" },
    };

    [Theory]
    [MemberData(nameof(Paths))]
    public void TempEn83_ReconoceLasDosFormas(string file, string expected)
    {
        var scrubber = CrashReportScrubber.FromEnvironment(
            v => ShortEnvironment.GetValueOrDefault(v), () => ShortTemp + @"\", ToLong);

        string scrubbed = scrubber.Scrub("Error al abrir " + file);

        Assert.Equal("Error al abrir " + expected, scrubbed);
        Assert.DoesNotContain("Boda", scrubbed, StringComparison.Ordinal);
        Assert.DoesNotContain("Ana Pérez", scrubbed, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public void SinVariables_ReconoceLaCarpetaTemporalEnLasDosFormas(string file, string expected)
    {
        // Sin TEMP ni TMP en el entorno queda Path.GetTempPath(), que termina en barra.
        var scrubber = CrashReportScrubber.FromEnvironment(
            v => v is "TEMP" or "TMP" ? null : ShortEnvironment.GetValueOrDefault(v), () => ShortTemp + @"\", ToLong);

        Assert.Equal(expected, scrubber.Scrub(file));
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public void TempLargo_ReconoceTambienLaForma83(string file, string expected)
    {
        // Al revés: la variable en nombre largo y la ruta en 8.3, con la carpeta temporal del sistema en 8.3.
        var environment = new Dictionary<string, string?>(ShortEnvironment, StringComparer.Ordinal)
        {
            ["TEMP"] = LongTemp,
            ["TMP"] = LongTemp,
        };
        var scrubber = CrashReportScrubber.FromEnvironment(
            v => environment.GetValueOrDefault(v), () => ShortTemp + @"\", ToLong);

        Assert.Equal(expected, scrubber.Scrub(file));
    }

    [Fact]
    public void SiLaExpansionFalla_QuedaLaFormaQueHay()
    {
        var scrubber = CrashReportScrubber.FromEnvironment(
            v => ShortEnvironment.GetValueOrDefault(v), () => ShortTemp + @"\", _ => throw new IOException("sin acceso"));

        Assert.Equal(@"%TEMP%\…\clip 1.mp4", scrubber.Scrub(ShortTemp + @"\Boda Pérez\clip 1.mp4"));
    }

    [Fact]
    public void FueraDeWindows_LaExpansionDevuelveLaEntrada()
    {
        if (OperatingSystem.IsWindows())
        {
            // En Windows una ruta que no existe tampoco se expande: devuelve la entrada.
            Assert.Equal(@"Z:\NOEXIS~1\Temp", CrashLongPathName.Expand(@"Z:\NOEXIS~1\Temp"));
            return;
        }

        Assert.Equal(ShortTemp, CrashLongPathName.Expand(ShortTemp));
    }
}
