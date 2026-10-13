using System.Diagnostics;
using Rodaje.App.Services.Crashes;

namespace Rodaje.App.Tests.Crashes;

/// <summary>
/// Tarea 135: el limpiador funciona con una lista blanca (de cualquier otra ruta absoluta queda solo el último segmento),
/// tapa el nombre del usuario y del equipo, y no devuelve el texto original cuando no puede limpiarlo.
/// </summary>
public class CrashReportScrubberPrivacyTests
{
    private static readonly Dictionary<string, string?> AnaFolders = new()
    {
        ["%USERPROFILE%"] = @"C:\Users\ana",
        ["%LOCALAPPDATA%"] = @"C:\Users\ana\AppData\Local",
        ["%APPDATA%"] = @"C:\Users\ana\AppData\Roaming",
        ["%TEMP%"] = @"C:\Users\ana\AppData\Local\Temp\",
    };

    private static readonly CrashReportScrubber Ana = new(AnaFolders);

    [Theory]
    [InlineData(@"Leyendo D:\Clientes\Boda Lopez\toma1.bin", "Leyendo toma1.bin")]
    [InlineData(@"Exportando a E:\Entregas\Cliente Acme\", @"Exportando a …\")]
    [InlineData(@"Exportando a E:\Entregas\Cliente Acme\ (3 clips)", @"Exportando a …\ (3 clips)")]
    [InlineData(@"Abriendo \\ANA-PC\Compartida\x.txt", "Abriendo x.txt")]
    [InlineData(@"Fuente file:///D:/Clientes/Acme%20Corp/x.json", "Fuente file:///x.json")]
    [InlineData(@"Fuente file://servidor/Clientes/Acme/x.json", "Fuente file://x.json")]
    [InlineData(@"Ruta \\?\UNC\srv\Clientes\Acme\x.bin", "Ruta x.bin")]
    [InlineData(@"{""p"":""\\\\srv\\Clientes\\Acme\\x.json"",""q"":""E:\\Acme\\notas""}", @"{""p"":""x.json"",""q"":""notas""}")]
    [InlineData(@"Sin extensión D:\Clientes\Acme\notas de la boda", "Sin extensión notas de la boda")]
    [InlineData(@"Lista C:\Clientes\Acme\bin;C:\Windows\System32", @"Lista bin;C:\Windows\System32")]
    public void RutasFueraDeLaListaBlanca_QuedaSoloElUltimoSegmento(string line, string expected) =>
        Assert.Equal(expected, Ana.Scrub(line));

    [Theory]
    [InlineData(@"Nube C:\Users\ana\OneDrive - Acme Corp\Docs\a.json", "Nube a.json")]
    [InlineData(@"Nube %USERPROFILE%\OneDrive - Acme Corp\Docs\a.json", "Nube a.json")]
    [InlineData(@"Nube C:\Users\otra\OneDrive - Acme Corp\Docs\a.json", "Nube a.json")]
    [InlineData(@"Nube %OneDrive%\Clientes Acme\a.json", "Nube a.json")]
    public void DentroDelPerfil_TampocoQuedanLasCarpetas(string line, string expected) =>
        Assert.Equal(expected, Ana.Scrub(line));

    [Theory]
    [InlineData(@"Perfil C:\Users\Ana Perez", "Perfil %USERPROFILE%")]
    [InlineData(@"Arranca ""C:\Users\Ana Perez"" --medir 3", @"Arranca ""%USERPROFILE%"" --medir 3")]
    public void PerfilConEspacioAlFinal_SeTapaEntero(string line, string expected) =>
        Assert.Equal(expected, Ana.Scrub(line));

    [Theory]
    [InlineData(@"Sesión de ANA-PC\ana iniciada", @"Sesión de %COMPUTERNAME%\%USERNAME% iniciada", "ana", "ANA-PC")]
    [InlineData("Usuario: Ana; equipo: ana-pc.", "Usuario: %USERNAME%; equipo: %COMPUTERNAME%.", "ana", "ANA-PC")]
    [InlineData("La banana de Anabel en %TEMP%", "La banana de Anabel en %TEMP%", "ana", "ANA-PC")]
    [InlineData(@"Nombres cortos: al PC\al", @"Nombres cortos: al PC\al", "al", "PC")]
    public void UsuarioYEquipo_FueraDeLasRutas_PasanASuVariable(string line, string expected, string userName, string machineName)
    {
        var scrubber = new CrashReportScrubber(AnaFolders, userName, machineName);

        Assert.Equal(expected, scrubber.Scrub(line));
    }

    [Fact]
    public void MuchasRaicesUncEnUnaLinea_SeLimpianSinDevolverElOriginal()
    {
        // Con la expresión de antes, 20 000 raíces vencían el plazo y salía el texto entero.
        string huge = "Ruta " + string.Concat(Enumerable.Repeat(@"\\a\", 20_000)) + "fin";
        string fits = "Ruta " + string.Concat(Enumerable.Repeat(@"\\a\", 2_000)) + "fin";
        var watch = Stopwatch.StartNew();

        string text = Ana.Scrub(huge);

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"tardó {watch.Elapsed}");
        Assert.True(text.Length < 100, $"quedaron {text.Length} caracteres");
        Assert.DoesNotContain(@"\\a\", text, StringComparison.Ordinal);
        Assert.Equal("Ruta fin", Ana.Scrub(fits));
    }

    [Fact]
    public void SiSeAgotaElTiempo_SeTapaEnVezDeDevolverElOriginal()
    {
        var scrubber = new CrashReportScrubber(AnaFolders) { Budget = TimeSpan.Zero };

        string text = scrubber.Scrub("Importó D:\\Clientes\\Acme\\toma.bin\r\nsegunda línea");

        Assert.DoesNotContain("Acme", text, StringComparison.Ordinal);
        Assert.Equal(CrashReportScrubber.OutOfTimeLine + "\n" + CrashReportScrubber.OutOfTimeLine, text);
    }

    [Fact]
    public void VariasLineas_ConservanSusSaltos()
    {
        string text = Ana.Scrub("Uno D:\\Clientes\\Acme\\a.bin\r\n\r\nDos E:\\Acme\\b.bin\nTres\n");

        Assert.Equal("Uno a.bin\r\n\r\nDos b.bin\nTres\n", text);
    }

    [Fact]
    public void UnaLineaLarga_SeRecortaYLaRutaDelFinalNoSale()
    {
        string line = new string('x', CrashReportScrubber.MaxLineLength + 10) + @" D:\Clientes\Acme\a.bin";

        string text = Ana.Scrub(line + "\nsigue");

        Assert.DoesNotContain("Acme", text, StringComparison.Ordinal);
        Assert.EndsWith("\nsigue", text, StringComparison.Ordinal);
        Assert.Contains("[recortado: ", text, StringComparison.Ordinal);
    }
}
