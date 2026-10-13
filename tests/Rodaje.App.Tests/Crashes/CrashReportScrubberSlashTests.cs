using Rodaje.App.Services.Crashes;

namespace Rodaje.App.Tests.Crashes;

/// <summary>
/// Tarea 166: con «/» como barra de la ruta (URL <c>file:///</c>, argumentos de ffmpeg), el segmento que ya parece texto
/// termina la ruta, igual que con la otra barra: la «/» de «y/o» o de «12/34» no convierte «a.mp4 y» en una carpeta ni se
/// come la frase. En <c>%TEMP%</c> solo se conservan las carpetas que empiezan por «rodaje-».
/// </summary>
public class CrashReportScrubberSlashTests
{
    private static readonly Dictionary<string, string?> Temp = new() { ["%TEMP%"] = @"C:\Users\ana\AppData\Local\Temp\" };

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
    public void UnaCarpetaConExtensionAparenteYEspacio_SeTapa()
    {
        // Tarea 183: «Boda.v2 final» parece «archivo + texto», pero lo que sigue a su barra («a.mp4») continúa la ruta, así
        // que es una carpeta y se tapa entera (con #166 la ruta se cortaba ahí y quedaba «Boda.v2 final/a.mp4»).
        string text = Ana.Scrub("Copia D:/Clientes/Boda.v2 final/a.mp4");

        Assert.Equal("Copia a.mp4", text);
    }

    [Theory]
    [InlineData("Leyendo C:/Users/Maria.Lopez Garcia/Videos/a.mp4", "Leyendo a.mp4")]
    [InlineData("Leyendo D:/Clientes/Ana.Perez Boda/a.mp4", "Leyendo a.mp4")]
    [InlineData("Leyendo D:/Proyectos/Juan.Gomez Cliente/Secreto/a.mp4 y/o x", "Leyendo a.mp4 y/o x")]
    [InlineData("Leyendo C:/Users/ana/OneDrive - Acme S.A. Clientes/Boda/a.mp4", "Leyendo a.mp4")]
    [InlineData("Leyendo D:/Clientes/Ana.Perez Boda/Fotos de la boda.jpg y siguió", "Leyendo Fotos de la boda.jpg y siguió")]
    [InlineData("Leyendo D:/Clientes/Ana.Perez Boda/a.mp4 y/o respaldo", "Leyendo a.mp4 y/o respaldo")]
    [InlineData("Leyendo D:/Clientes/Ana Perez (2024)/a.mp4", "Leyendo a.mp4")]
    [InlineData(@"Leyendo D:\Clientes\Ana.Perez Boda/a.mp4", "Leyendo a.mp4")]
    public void ConBarraNormal_UnaCarpetaQueParezcaTextoSeTapa(string line, string expected)
    {
        // Tarea 183: con «/», un segmento como «Nombre.Apellido Otro» parecía «archivo + texto» y cortaba la ruta, así que
        // el perfil, la carpeta del cliente o la empresa quedaban a la vista. Ahora solo se corta si lo que sigue a la barra
        // no continúa la ruta.
        var scrubber = new CrashReportScrubber(
            new Dictionary<string, string?> { ["%USERPROFILE%"] = @"C:\Users\ana" }, userName: "ana");

        string text = scrubber.Scrub(line);

        Assert.Equal(expected, text);
        foreach (string word in new[] { "Maria", "Lopez", "Garcia", "Perez", "Juan", "Gomez", "Cliente", "Secreto", "Acme", "Videos" })
        {
            Assert.DoesNotContain(word, text, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("No existe la carpeta D:/Clientes/Acme S.A. Boda/Boda 2024 final", "No existe la carpeta Boda 2024 final")]
    [InlineData("D:/Clientes/Ana.Perez Boda/Boda de Sofia", "Boda de Sofia")]
    [InlineData("D:/Clientes/Acme S.A. 2024/10/12/a.mp4", "a.mp4")]
    [InlineData("D:/Clientes/Boda.v2 12/34/x", "x")]
    [InlineData("D:/Clientes/Boda.v2 12/34", "34")]
    [InlineData("Leyendo D:/Clientes/Ana Perez (2024)/notas finales", "Leyendo notas finales")]
    [InlineData("Leyendo D:/Clientes/Ana Perez  Boda/notas finales", "Leyendo notas finales")]
    [InlineData("Leyendo D:/Clientes/Acme S.A. Boda/ (12 s)", "Leyendo …/ (12 s)")]
    [InlineData("Leyendo C:/Users/ana/OneDrive - Acme S.A. Clientes/Boda final", "Leyendo Boda final")]
    [InlineData("Leyendo %USERPROFILE%/Documents/Ana.Perez Boda/Boda final", "Leyendo Boda final")]
    [InlineData(@"Leyendo D:\Clientes\Ana.Perez Boda/Boda de Sofia", "Leyendo Boda de Sofia")]
    [InlineData(@"Leyendo \\servidor\Clientes/Acme S.A. Boda/Boda final", "Leyendo Boda final")]
    [InlineData("Leyendo file:///D:/Clientes/Acme S.A. Boda/Boda final", "Leyendo file:///Boda final")]
    [InlineData("Leyendo file://servidor/Clientes/Acme S.A. Boda/Boda final", "Leyendo file://Boda final")]
    [InlineData("Leyendo D:/Clientes/Acme Corp. Boda/2024/x", "Leyendo x")]
    [InlineData("Leyendo D:/Clientes/Ana.Perez 2024/10/12", "Leyendo 12")]
    [InlineData("Leyendo D:/Clientes/Ana.Perez Boda/12/10/2026 y siguió", "Leyendo 2026 y siguió")]
    [InlineData("Leyendo D:/Clientes/Sofia.Lopez (copia)/notas varias", "Leyendo notas varias")]
    [InlineData(@"Leyendo \\?\D:/Clientes/Acme S.A. Boda/Boda final", "Leyendo Boda final")]
    [InlineData("Leyendo %ONEDRIVE%/Clientes/Acme S.A. Boda/Boda final", "Leyendo Boda final")]
    [InlineData("Leyendo C:/Users/ana/OneDrive/Documentos/Acme S.A. de C.V./Boda final", "Leyendo Boda final")]
    public void ConBarraNormal_UnTramoQueNoEsClaramenteFraseSeTapa(string line, string expected)
    {
        // Tarea 191: tras un segmento que parece texto («Acme S.A. Boda», «Ana.Perez Boda», «Boda.v2 12»), la ruta se
        // cortaba si lo que seguía a la «/» parecía frase o eran cifras, y ese segmento quedaba a la vista. Ahora solo se
        // corta tras un archivo con una extensión conocida («a.mp4 y/o respaldo»); si no, ante la duda, se tapa.
        var scrubber = new CrashReportScrubber(
            new Dictionary<string, string?> { ["%USERPROFILE%"] = @"C:\Users\ana" }, userName: "ana");

        string text = scrubber.Scrub(line);

        Assert.Equal(expected, text);
        foreach (string word in new[] { "Clientes", "Acme", "Perez", "Documents", "OneDrive", "servidor", "v2", "S.A.", "Lopez" })
        {
            Assert.DoesNotContain(word, text, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("Leyendo D:/Clientes/Boda/a.mp4 el 12/10/2026", "Leyendo a.mp4 el 12/10/2026")]
    [InlineData("Leyendo D:/Clientes/Boda/a.mp4 el 12/10/2026 y siguió", "Leyendo a.mp4 el 12/10/2026 y siguió")]
    [InlineData("Leyendo D:/Clientes/Boda/ajustes.json y/o respaldo", "Leyendo ajustes.json y/o respaldo")]
    [InlineData("Leyendo D:/Clientes/Boda/Foto.JPG y/o respaldo", "Leyendo Foto.JPG y/o respaldo")]
    public void ConBarraNormal_TrasUnArchivoConocidoLaFraseSeConserva(string line, string expected) =>
        Assert.Equal(expected, Ana.Scrub(line));

    [Theory]
    [InlineData(@"Temporal C:\Users\ana\AppData\Local\Temp\rodajeAna\x.bin", @"Temporal %TEMP%\…\x.bin")]
    [InlineData(@"Temporal C:\Users\ana\AppData\Local\Temp\Rodaje Boda\x.bin", @"Temporal %TEMP%\…\x.bin")]
    [InlineData(@"Temporal C:\Users\ana\AppData\Local\Temp\rodaje\x.bin", @"Temporal %TEMP%\…\x.bin")]
    [InlineData(@"Temporal C:\Users\ana\AppData\Local\Temp\rodaje-proxy\x.bin", @"Temporal %TEMP%\rodaje-proxy\x.bin")]
    [InlineData(@"Temporal C:\Users\ana\AppData\Local\Temp\Rodaje-Exportar\x.bin", @"Temporal %TEMP%\Rodaje-Exportar\x.bin")]
    public void EnTemp_SoloSeConservanLasCarpetasRodajeConGuion(string line, string expected) =>
        Assert.Equal(expected, Ana.Scrub(line));

    [Fact]
    public void LineasPatologicasConBarraNormal_SeLimpianSinTaparse()
    {
        // Sin tope de tiempo (tarea 178): aquí se comprueba el resultado; el coste, sin reloj, en la prueba siguiente.
        string line = PathologicalLine();
        string many = string.Join('\n', Enumerable.Repeat(line[..CrashReportScrubber.MaxLineLength], 20));
        var scrubber = new CrashReportScrubber(Temp) { Budget = TimeSpan.MaxValue };

        string text = scrubber.Scrub(line + "\n" + many);

        Assert.DoesNotContain(CrashReportScrubber.HiddenLine, text, StringComparison.Ordinal);
        Assert.DoesNotContain(CrashReportScrubber.OutOfTimeLine, text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("D:/a.b c/")]
    [InlineData("file:///D:/x y.mp4 y/o %TEMP%/a.b c/")]
    [InlineData("file://s/a.mp4 y\\b.mp4 z/")]
    [InlineData("")]
    public void ConBarraNormal_ElCosteCreceEnProporcionAlTexto(string unit)
    {
        // Como en CrashReportScrubberTextTests: el trabajo del analizador, no el reloj, al pasar de 1 KB a 8 KB por línea.
        string Repeat(int length) => unit.Length == 0 ? PathologicalLine()[..length] : string.Concat(Enumerable.Repeat(unit, (length / unit.Length) + 1))[..length];
        var scrubber = new CrashReportScrubber(Temp) { Budget = TimeSpan.MaxValue };

        scrubber.Scrub(Repeat(1024), out long small);
        scrubber.Scrub(Repeat(CrashReportScrubber.MaxLineLength), out long large);

        Assert.InRange(large, 1, 4L * CrashReportScrubber.MaxLineLength);
        Assert.InRange((double)large / small, 1, 8 * 1.25);
    }

    private static string PathologicalLine() =>
        string.Concat(Enumerable.Repeat("D:/a.b c/", 2_000))
        + string.Concat(Enumerable.Repeat("file:///D:/x y.mp4 y/o %TEMP%/a.b c/", 1_000));
}
