using System.Diagnostics;
using Rodaje.App.Services.Crashes;

namespace Rodaje.App.Tests.Crashes;

/// <summary>
/// Tarea 166: con «/» como barra de la ruta (URL <c>file:///</c>, argumentos de ffmpeg), el segmento que ya parece texto
/// termina la ruta, igual que con la otra barra: la «/» de «y/o» o de «12/34» no convierte «a.mp4 y» en una carpeta ni se
/// come la frase. En <c>%TEMP%</c> solo se conservan las carpetas que empiezan por «rodaje-».
/// </summary>
public class CrashReportScrubberSlashTests
{
    private static readonly CrashReportScrubber Ana = new(new Dictionary<string, string?>
    {
        ["%USERPROFILE%"] = @"C:\Users\ana",
        ["%LOCALAPPDATA%"] = @"C:\Users\ana\AppData\Local",
        ["%APPDATA%"] = @"C:\Users\ana\AppData\Roaming",
        ["%TEMP%"] = @"C:\Users\ana\AppData\Local\Temp\",
    });

    [Theory]
    [InlineData("Leyendo D:/Clientes/Boda Lopez/a.mp4 y/o respaldo", "Leyendo a.mp4 y/o respaldo")]
    [InlineData("Leyendo D:/Clientes/Boda Lopez/a.mp4 (formato / corrupto) listo", "Leyendo a.mp4 (formato / corrupto) listo")]
    [InlineData("Leyendo file:///D:/Clientes/Acme%20Corp/a.mp4 (error 12/34) y siguió", "Leyendo file:///a.mp4 (error 12/34) y siguió")]
    [InlineData("Leyendo file:///C:/Users/Ana%20Perez/Videos/a.mp4 y/o respaldo", "Leyendo file:///a.mp4 y/o respaldo")]
    [InlineData("Leyendo file://servidor/Clientes/Acme/a.mp4 y/o respaldo", "Leyendo file://a.mp4 y/o respaldo")]
    [InlineData("ffmpeg -i D:/Clientes/Boda Lopez/a.mp4 -c copy D:/Clientes/Boda Lopez/b.mkv  12/300", "ffmpeg -i a.mp4 -c copy b.mkv  12/300")]
    [InlineData("Leyendo 'D:/Clientes/Boda Lopez/a.mp4' y/o respaldo", "Leyendo 'a.mp4' y/o respaldo")]
    public void ConBarraNormal_ElTextoTrasLaRutaSeConserva(string line, string expected)
    {
        string text = Ana.Scrub(line);

        Assert.Equal(expected, text);
        Assert.DoesNotContain("Clientes", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Perez", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"Leyendo D:/Clientes\Boda Lopez\a.mp4 y/o respaldo", "Leyendo a.mp4 y/o respaldo")]
    [InlineData(@"Leyendo D:\Clientes/Boda Lopez/a.mp4 y/o respaldo", "Leyendo a.mp4 y/o respaldo")]
    [InlineData(@"Leyendo D:/Clientes/Boda Lopez/a.mp4 (copia \ original) y 12/34", @"Leyendo a.mp4 (copia \ original) y 12/34")]
    [InlineData(@"Leyendo file:///D:/Clientes\Acme%20Corp/a.mp4 (error 12/34) y siguió", "Leyendo file:///a.mp4 (error 12/34) y siguió")]
    public void ConBarrasMezcladas_ElTextoTrasLaRutaSeConserva(string line, string expected)
    {
        string text = Ana.Scrub(line);

        Assert.Equal(expected, text);
        Assert.DoesNotContain("Clientes", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Copia D:/Clientes/Boda Lopez/proxies/a.bin", "Copia a.bin")]
    [InlineData("Copia D:/Clientes/v1.2 final/a.mp4", "Copia a.mp4")]
    [InlineData("Perfil C:/Users/Ana Perez/AppData/Local/x.json", "Perfil %USERPROFILE%/AppData/Local/x.json")]
    public void ConBarraNormal_LasCarpetasConEspaciosSeTapan(string line, string expected) =>
        Assert.Equal(expected, Ana.Scrub(line));

    [Fact]
    public void UnaCarpetaConExtensionAparenteYEspacio_CortaLaRuta()
    {
        // El coste de la regla, fijado: «Boda.v2 final» parece «archivo + texto», así que la ruta termina en «Boda.v2» y
        // lo que sigue a la carpeta queda como texto. Lo de antes de ella sí se tapa.
        string text = Ana.Scrub("Copia D:/Clientes/Boda.v2 final/a.mp4");

        Assert.Equal("Copia Boda.v2 final/a.mp4", text);
        Assert.DoesNotContain("Clientes", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"Temporal C:\Users\ana\AppData\Local\Temp\rodajeAna\x.bin", @"Temporal %TEMP%\…\x.bin")]
    [InlineData(@"Temporal C:\Users\ana\AppData\Local\Temp\Rodaje Boda\x.bin", @"Temporal %TEMP%\…\x.bin")]
    [InlineData(@"Temporal C:\Users\ana\AppData\Local\Temp\rodaje\x.bin", @"Temporal %TEMP%\…\x.bin")]
    [InlineData(@"Temporal C:\Users\ana\AppData\Local\Temp\rodaje-proxy\x.bin", @"Temporal %TEMP%\rodaje-proxy\x.bin")]
    [InlineData(@"Temporal C:\Users\ana\AppData\Local\Temp\Rodaje-Exportar\x.bin", @"Temporal %TEMP%\Rodaje-Exportar\x.bin")]
    public void EnTemp_SoloSeConservanLasCarpetasRodajeConGuion(string line, string expected) =>
        Assert.Equal(expected, Ana.Scrub(line));

    [Fact]
    public void LineasPatologicasConBarraNormal_SeLimpianEnPocoTiempo()
    {
        string line = string.Concat(Enumerable.Repeat("D:/a.b c/", 2_000))
            + string.Concat(Enumerable.Repeat("file:///D:/x y.mp4 y/o %TEMP%/a.b c/", 1_000));
        // Pocas líneas y un presupuesto propio: en la CI de Windows (con cobertura) corre a la vez que la prueba
        // patológica de CrashReportScrubberTextTests, y las dos comparten el presupuesto de 2 s por llamada.
        string many = string.Join('\n', Enumerable.Repeat(line[..CrashReportScrubber.MaxLineLength], 20));
        var scrubber = new CrashReportScrubber(new Dictionary<string, string?> { ["%TEMP%"] = @"C:\Users\ana\AppData\Local\Temp\" })
        {
            Budget = TimeSpan.FromSeconds(30),
        };
        var watch = Stopwatch.StartNew();

        string text = scrubber.Scrub(line + "\n" + many);

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"tardó {watch.Elapsed}");
        Assert.DoesNotContain(CrashReportScrubber.HiddenLine, text, StringComparison.Ordinal);
        Assert.DoesNotContain(CrashReportScrubber.OutOfTimeLine, text, StringComparison.Ordinal);
    }
}
