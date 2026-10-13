using System.Diagnostics;
using Rodaje.App.Services.Crashes;

namespace Rodaje.App.Tests.Crashes;

/// <summary>
/// Tarea 153: el limpiador no se come el texto que sigue a una ruta. Una ruta con «\» no termina en la siguiente «/» de la
/// frase («y/o», «12/10/2026»), y el último segmento acaba en su extensión o ante « (» y dos espacios. También tapa las
/// rutas con <c>%SystemDrive%</c>, las de <c>\Device\HarddiskVolumeN</c> y las subcarpetas ajenas de <c>%TEMP%</c>.
/// </summary>
public class CrashReportScrubberTextTests
{
    private static readonly CrashReportScrubber Ana = new(new Dictionary<string, string?>
    {
        ["%USERPROFILE%"] = @"C:\Users\ana",
        ["%LOCALAPPDATA%"] = @"C:\Users\ana\AppData\Local",
        ["%APPDATA%"] = @"C:\Users\ana\AppData\Roaming",
        ["%TEMP%"] = @"C:\Users\ana\AppData\Local\Temp\",
    });

    [Theory]
    [InlineData(@"Error al leer D:\Clientes\Boda\a.mp4 (formato no válido / corrupto) y se omitió",
        "Error al leer a.mp4 (formato no válido / corrupto) y se omitió")]
    [InlineData(@"Frame 12/300 de 'D:\Clientes\Boda\a.mp4' (4%) procesado el 12/10/2026 a las 10:00",
        "Frame 12/300 de 'a.mp4' (4%) procesado el 12/10/2026 a las 10:00")]
    [InlineData(@"C:\Users\Ana Perez\Videos\x.mp4 y/o el respaldo falló", "x.mp4 y/o el respaldo falló")]
    [InlineData(@"Leído c:/Clientes/Boda/a.mp4 (copia \ original) bien", @"Leído a.mp4 (copia \ original) bien")]
    [InlineData(@"Proxy \\nas\Clientes\Boda\a.mp4 listo, 3/4 hechos", "Proxy a.mp4 listo, 3/4 hechos")]
    [InlineData(@"{""p"":""D:\\Clientes\\Boda\\a.mp4"",""r"":""1/2""}", @"{""p"":""a.mp4"",""r"":""1/2""}")]
    public void ElTextoTrasLaRuta_SeConserva(string line, string expected) =>
        Assert.Equal(expected, Ana.Scrub(line));

    [Theory]
    [InlineData(@"Perfil C:\Users\Ana Perez (sesión 2) cerrada", "Perfil %USERPROFILE% (sesión 2) cerrada")]
    [InlineData(@"Perfil C:\Users\Ana Perez  siguiente campo", "Perfil %USERPROFILE%  siguiente campo")]
    [InlineData(@"Perfil C:\Users\ana.lopez y luego falló", "Perfil %USERPROFILE% y luego falló")]
    public void ElPerfilAlFinal_NoSeComeLoQueSigue(string line, string expected) =>
        Assert.Equal(expected, Ana.Scrub(line));

    [Theory]
    [InlineData(@"Mixta D:\Clientes\Acme/proxies/a.bin", "Mixta a.bin")]
    [InlineData(@"Mixta D:\Clientes\Boda Lopez/proxies/a.bin", "Mixta a.bin")]
    [InlineData(@"Mixta C:\Users\Ana Perez/AppData/Local/x.json", "Mixta %USERPROFILE%/AppData/Local/x.json")]
    [InlineData(@"Error D:\Clientes\Boda\a.mp4 (no válido \ corrupto)", @"Error …\ corrupto)")]
    public void BarrasMezcladas_NoDejanCarpetas(string line, string expected)
    {
        string text = Ana.Scrub(line);

        Assert.Equal(expected, text);
        Assert.DoesNotContain("Clientes", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Perez", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"Perfil %SystemDrive%\Users\ana\Videos\Boda\a.mp4", "Perfil a.mp4")]
    [InlineData(@"Perfil %HOMEDRIVE%\Users\Ana Perez\AppData\Local\x.log", @"Perfil %USERPROFILE%\AppData\Local\x.log")]
    [InlineData(@"Datos %SystemDrive%\Clientes\Acme\x.json", "Datos x.json")]
    [InlineData(@"Sistema %SystemDrive%\Windows\System32\d3d11.dll", @"Sistema %SystemDrive%\Windows\System32\d3d11.dll")]
    [InlineData(@"Volumen \Device\HarddiskVolume3\Users\ana\Videos\a.mp4", "Volumen a.mp4")]
    [InlineData(@"Volumen \Device\HarddiskVolume3\Users\ana", "Volumen %USERPROFILE%")]
    [InlineData(@"Volumen \Device\HarddiskVolume12\Program Files\Rodaje\Rodaje.exe", @"Volumen \Device\HarddiskVolume12\Program Files\Rodaje\Rodaje.exe")]
    [InlineData(@"Temporal C:\Users\ana\AppData\Local\Temp\Boda Pérez\proxies\a.bin", @"Temporal %TEMP%\…\a.bin")]
    [InlineData(@"Temporal %TEMP%\Clientes Acme\a.bin", @"Temporal %TEMP%\…\a.bin")]
    [InlineData(@"Temporal C:\Users\ana\AppData\Local\Temp\rodaje-exportar-3\parte 1.mp4", @"Temporal %TEMP%\rodaje-exportar-3\parte 1.mp4")]
    [InlineData(@"Temporal C:\Users\ana\AppData\Local\Temp\x.log", @"Temporal %TEMP%\x.log")]
    public void FugasMenores_SeTapan(string line, string expected)
    {
        string text = Ana.Scrub(line);

        Assert.Equal(expected, text);
        Assert.DoesNotContain("Acme", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Boda", text, StringComparison.Ordinal);
    }

    [Fact]
    public void LineasPatologicasConBarrasYEspacios_SeLimpianEnPocoTiempo()
    {
        // Muchas rutas cuyo último segmento lleva espacios, extensiones y barras de la otra clase.
        string line = string.Concat(Enumerable.Repeat(@"D:\a b.mp4 (x / y.z  w\ ", 1_000))
            + string.Concat(Enumerable.Repeat(@"%SystemDrive%\x y/", 1_000))
            + string.Concat(Enumerable.Repeat(@"\Device\HarddiskVolume1\a.b c/", 1_000));
        string many = string.Join('\n', Enumerable.Repeat(line[..CrashReportScrubber.MaxLineLength], 200));
        var watch = Stopwatch.StartNew();

        string text = Ana.Scrub(line + "\n" + many);

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"tardó {watch.Elapsed}");
        Assert.DoesNotContain(CrashReportScrubber.HiddenLine, text, StringComparison.Ordinal);
        Assert.DoesNotContain(CrashReportScrubber.OutOfTimeLine, text, StringComparison.Ordinal);
    }
}
