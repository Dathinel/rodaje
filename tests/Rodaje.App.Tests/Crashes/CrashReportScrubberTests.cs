using System.Diagnostics;
using Rodaje.App.Services.Crashes;
using Rodaje.App.Tests.Support;

namespace Rodaje.App.Tests.Crashes;

/// <summary>
/// Tarea 108: el informe de fallo se pega en GitHub (público), así que no lleva el nombre del usuario en las rutas ni las
/// carpetas de sus medios. Rutas de ejemplo como las de un Windows de verdad: con espacios, tildes, UNC y «/».
/// </summary>
public class CrashReportScrubberTests
{
    private static readonly CrashReportScrubber Ana = new(new Dictionary<string, string?>
    {
        ["%USERPROFILE%"] = @"C:\Users\ana",
        ["%LOCALAPPDATA%"] = @"C:\Users\ana\AppData\Local",
        ["%APPDATA%"] = @"C:\Users\ana\AppData\Roaming",
        ["%TEMP%"] = @"C:\Users\ana\AppData\Local\Temp\",
        ["%HOMEPATH%"] = null,
    });

    [Theory]
    [InlineData(@"Importó C:\Users\ana\Videos\Boda de María\Ñandú final.mp4 (12 s)", "Importó Ñandú final.mp4 (12 s)")]
    [InlineData(@"No se pudo leer E:\Clientes\Acme Corp\toma 03.MOV: formato", "No se pudo leer toma 03.MOV: formato")]
    [InlineData(@"Proxy de \\nas\videos\Familia 2025\cumpleaños.mkv listo", "Proxy de cumpleaños.mkv listo")]
    [InlineData(@"Ruta larga \\?\C:\Users\ana\Videos\largo.mp4", "Ruta larga largo.mp4")]
    [InlineData(@"Abre c:/users/ana/Música/canción.flac y D:\Fotos\playa.JPG", "Abre canción.flac y playa.JPG")]
    [InlineData(@"{""path"":""C:\\Users\\ana\\Videos\\clip.mp4""}", @"{""path"":""clip.mp4""}")]
    [InlineData(@"Guardado C:\Users\ana\Documents\Proyectos\Boda.rodaje", "Guardado Boda.rodaje")]
    [InlineData(@"LUT E:\Looks\cálido.cube, subtítulos C:\Users\ana\Desktop\es.srt", "LUT cálido.cube, subtítulos es.srt")]
    public void Media_QuedaSoloElNombreDelArchivo(string line, string expected) =>
        Assert.Equal(expected, Ana.Scrub(line));

    [Theory]
    [InlineData(@"Caché en C:\Users\ana\AppData\Local\Temp\rodaje-proxy\a.bin", @"Caché en %TEMP%\rodaje-proxy\a.bin")]
    [InlineData(@"Datos en C:\Users\ana\AppData\Local\Rodaje\fallos", @"Datos en %LOCALAPPDATA%\Rodaje\fallos")]
    [InlineData(@"Ajustes c:\USERS\ANA\appdata\roaming\Rodaje\ajustes.json", @"Ajustes %APPDATA%\Rodaje\ajustes.json")]
    [InlineData(@"Perfil C:/Users/ana/AppData/Local/Temp/x.log", "Perfil %TEMP%/x.log")]
    [InlineData(@"Escritorio C:\Users\ana\Desktop\notas.txt", @"Escritorio %USERPROFILE%\Desktop\notas.txt")]
    public void CarpetasDelUsuario_PasanASuVariable(string line, string expected) =>
        Assert.Equal(expected, Ana.Scrub(line));

    [Fact]
    public void LineaDeComandos_SinPerfilNiCarpetaDelProyecto()
    {
        string line = @"10:00:00.000 Rodaje 1.0.0+abc arranca: ""C:\Users\ana\AppData\Local\Programs\Rodaje\Rodaje.exe"" ""C:\Users\ana\Documents\Mis proyectos\Boda Pérez.rodaje"" --medir 3";

        Assert.Equal(
            @"10:00:00.000 Rodaje 1.0.0+abc arranca: ""%LOCALAPPDATA%\Programs\Rodaje\Rodaje.exe"" ""Boda Pérez.rodaje"" --medir 3",
            Ana.Scrub(line));
    }

    [Theory]
    [InlineData(@"   at Rodaje.Core.Timing.Clock.Tick() in D:\a\rodaje\rodaje\src\Rodaje.Core\Timing\Clock.cs:line 42")]
    [InlineData(@"   at Rodaje.App.Program.Main(String[] args) in /home/runner/work/rodaje/src/Rodaje.App/Program.cs:line 131")]
    [InlineData(@"Ana dijo que la banana estaba en Ana\notas sin ruta")]
    [InlineData(@"Carpeta pública C:\Users\Public\Videos\muestras")]
    [InlineData(@"Instalado en C:\Program Files\Rodaje\Rodaje.exe")]
    [InlineData("Código 0xC000027B: error «stowed» de WinRT.")]
    public void LoQueNoEsDelUsuario_QuedaIgual(string line) =>
        Assert.Equal(line, Ana.Scrub(line));

    [Theory]
    [InlineData(@"Otro usuario C:\Users\anabel\notas.txt", @"Otro usuario %USERPROFILE%\notas.txt")]
    [InlineData(@"Nombre corto C:\Users\DANIEL~1\AppData\Local\Temp\x.log", @"Nombre corto %USERPROFILE%\AppData\Local\Temp\x.log")]
    [InlineData(@"Con espacio D:\Users\Ana María\Escritorio\lista.txt", @"Con espacio %USERPROFILE%\Escritorio\lista.txt")]
    [InlineData(@"Al final C:\Users\josé", "Al final %USERPROFILE%")]
    [InlineData(@"Viejo C:\Documents and Settings\pepe\Escritorio", @"Viejo %USERPROFILE%\Escritorio")]
    public void OtroPerfil_TambienSeTapa(string line, string expected) =>
        Assert.Equal(expected, Ana.Scrub(line));

    [Fact]
    public void ElNombreDentroDeOtroPerfil_NoSeCortaPorLaMitad()
    {
        string text = Ana.Scrub(@"Perfil C:\Users\anabel\Documents y C:\Users\ana\Documents; banana");

        Assert.Equal(@"Perfil %USERPROFILE%\Documents y %USERPROFILE%\Documents; banana", text);
        Assert.DoesNotContain("bel", text, StringComparison.Ordinal);
    }

    [Fact]
    public void PerfilEnUnServidor_PasaASuVariable()
    {
        var scrubber = new CrashReportScrubber(new Dictionary<string, string?> { ["%USERPROFILE%"] = @"\\srv\perfiles$\ana.lopez" });

        Assert.Equal(@"Datos en %USERPROFILE%\AppData\x.json", scrubber.Scrub(@"Datos en \\SRV\perfiles$\Ana.Lopez\AppData\x.json"));
    }

    [Fact]
    public void UnaLineaEnorme_SeLimpiaEnPocoTiempo()
    {
        string line = @"C:\" + new string('a', 200_000) + " " + string.Concat(Enumerable.Repeat(@"C:\x y\", 20_000));
        var watch = Stopwatch.StartNew();

        string text = Ana.Scrub(line);

        Assert.NotNull(text);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"tardó {watch.Elapsed}");
    }

    [Fact]
    public void ElInformeEscrito_NoLlevaRutasDelUsuario()
    {
        using var dir = new TempDirectory();
        var log = new SessionLog();
        log.Append(@"Rodaje arranca: ""C:\Users\pruebas\AppData\Local\Programs\Rodaje\Rodaje.exe"" ""C:\Users\pruebas\Documents\Boda Pérez.rodaje""");
        log.Append(@"Importó \\nas\familia\Vacaciones 2025\playa ñandú.mov");
        var reporter = new CrashReporter(dir.Path, "1.0.0+prueba", log);

        string? path = reporter.Write(CrashKind.Caught,
            new IOException(@"No se encontró E:\Clientes\Acme\toma 01.mp4 (C:\Users\pruebas\AppData\Roaming\Rodaje\ajustes.json)"), fatal: true);

        Assert.NotNull(path);
        string text = File.ReadAllText(path);
        Assert.DoesNotContain("pruebas", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Acme", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Vacaciones", text, StringComparison.Ordinal);
        Assert.Contains("Boda Pérez.rodaje", text, StringComparison.Ordinal);
        Assert.Contains("playa ñandú.mov", text, StringComparison.Ordinal);
        Assert.Contains("No se encontró toma 01.mp4", text, StringComparison.Ordinal);
        Assert.Contains(@"%USERPROFILE%\AppData\Roaming\Rodaje\ajustes.json", text, StringComparison.Ordinal);
    }

    [Fact]
    public void LaSesionRecuperada_TampocoLlevaRutasDelUsuario()
    {
        using var dir = new TempDirectory();
        var crashedLog = new SessionLog();
        var crashed = new CrashReporter(dir.Path, "1.0.0+prueba", crashedLog);
        crashed.StartSession(4242, 1);
        crashedLog.Append(@"Exportando a C:\Users\pruebas\Videos\Final boda.mp4");
        crashedLog.Dispose();

        var reporter = new CrashReporter(dir.Path, "1.0.0+prueba", new SessionLog());
        string report = Assert.Single(reporter.RecoverUncleanSessions((_, _) => false));

        string text = File.ReadAllText(report);
        Assert.Contains("Exportando a Final boda.mp4", text, StringComparison.Ordinal);
        Assert.DoesNotContain("pruebas", text, StringComparison.Ordinal);
    }
}
