using Rodaje.App.Services.Crashes;

namespace Rodaje.App.Tests.Crashes;

/// <summary>
/// La línea «Minivolcado: …» con <c>%TEMP%</c> en su nombre largo, con espacios y tildes (el PC del autor; el runner de
/// la CI lo trae en 8.3 y no llega a esta regla): el limpiador tapa las carpetas de <c>%TEMP%</c> que no son
/// <c>rodaje-…</c>, y la ruta esperada de las pruebas debe hacer lo mismo.
/// </summary>
public sealed class CrashDumpPathTempTests
{
    private static readonly Dictionary<string, string?> Environment = new(StringComparer.Ordinal)
    {
        ["TEMP"] = @"C:\Users\Ana Pérez\AppData\Local\Temp",
        ["TMP"] = @"C:\Users\Ana Pérez\AppData\Local\Temp",
        ["LOCALAPPDATA"] = @"C:\Users\Ana Pérez\AppData\Local",
        ["APPDATA"] = @"C:\Users\Ana Pérez\AppData\Roaming",
        ["USERPROFILE"] = @"C:\Users\Ana Pérez",
    };

    [Theory]
    [InlineData(@"C:\Users\Ana Pérez\AppData\Local\Temp\rodaje pruebas app\carpeta ñandú 1a2b3c4d\2026-10-13_16-01-02-941.dmp",
        @"%TEMP%\…\2026-10-13_16-01-02-941.dmp")]
    [InlineData(@"C:\Users\Ana Pérez\AppData\Local\Temp\rodaje-pruebas\2026-10-13_16-01-02-941.dmp",
        @"%TEMP%\rodaje-pruebas\2026-10-13_16-01-02-941.dmp")]
    [InlineData(@"C:\Users\Ana Pérez\AppData\Local\Rodaje\fallos\2026-10-13_16-01-02-941.dmp",
        @"%LOCALAPPDATA%\Rodaje\fallos\2026-10-13_16-01-02-941.dmp")]
    public void LineaDelMinivolcado_ConTempLargoConEspaciosYTildes(string dump, string expected)
    {
        var scrubber = new CrashReportScrubber(Environment.Select(e => new KeyValuePair<string, string?>(
            e.Key == "TMP" ? "%TEMP%" : "%" + e.Key + "%", e.Value)));

        Assert.Equal("Minivolcado: " + expected, scrubber.Scrub("Minivolcado: " + dump));
        Assert.Equal(expected, ExpectedDumpPath.Of(dump, v => Environment.GetValueOrDefault(v)));
    }
}
