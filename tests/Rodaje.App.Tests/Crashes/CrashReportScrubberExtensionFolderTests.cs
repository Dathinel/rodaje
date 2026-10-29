using Rodaje.App.Services.Crashes;

namespace Rodaje.App.Tests.Crashes;

/// <summary>
/// Tarea 196: con «/» como barra, tras una carpeta que parece «archivo con extensión conocida + texto» («Informe.log
/// final», «Boda 2024.mp4 final»), la ruta se cortaba si lo que seguía a la barra llevaba espacios, y quedaban a la vista
/// esa carpeta y lo que la seguía. Ahora la «/» solo deja de separar si lo que sigue es claramente frase (empieza por un
/// conector corto como el de «y/o» y no la sigue otra barra); y, como no se sabe si ese último tramo es frase o nombre,
/// también se tapa si no parece un archivo.
/// </summary>
public class CrashReportScrubberExtensionFolderTests
{
    private static readonly Dictionary<string, string?> AnaFolders = new() { ["%USERPROFILE%"] = @"C:\Users\ana" };

    private static readonly CrashReportScrubber Ana = new(AnaFolders, userName: "ana");

    [Theory]
    [InlineData("Leyendo D:/Clientes/Informe.log final/Secreto confidencial", "Leyendo …")]
    [InlineData("Leyendo D:/Clientes/Boda 2024.mp4 final/Contrato Perez", "Leyendo …")]
    [InlineData("Leyendo D:/Clientes/Entrega.zip final/Contrato Perez", "Leyendo …")]
    [InlineData("Leyendo D:/Clientes/Boda.mov respaldo/Secreto confidencial (12 s)", "Leyendo … (12 s)")]
    [InlineData("Leyendo D:/Clientes/Boda.mov respaldo/Secreto/a.mp4", "Leyendo a.mp4")]
    [InlineData("Leyendo D:/Clientes/Entrega.zip final/Contrato Perez/a.mp4 y/o respaldo", "Leyendo a.mp4 y/o respaldo")]
    [InlineData("Leyendo D:/Clientes/Boda.mov y/o Perez/a.mp4", "Leyendo a.mp4")]
    [InlineData("Leyendo file:///D:/Clientes/Boda.mov respaldo/Contrato Perez", "Leyendo file:///…")]
    [InlineData(@"Leyendo D:\Clientes\Boda.mov respaldo/Contrato Perez", "Leyendo …")]
    [InlineData("Leyendo C:/Users/ana/Videos/Informe.log final/Secreto confidencial", "Leyendo …")]
    public void TrasUnaCarpetaArchivoMasTexto_LaRutaSeTapa(string line, string expected)
    {
        string text = Ana.Scrub(line);

        Assert.Equal(expected, text);
        foreach (string word in new[] { "Clientes", "Informe", "Boda", "Entrega", "Secreto", "Contrato", "Perez", "final", "respaldo/", "Videos" })
        {
            Assert.DoesNotContain(word, text, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("Leyendo D:/Clientes/Boda/a.mp4 y/o respaldo", "Leyendo a.mp4 y/o respaldo")]
    [InlineData("Leyendo D:/Clientes/Boda/a.mp4 y/o", "Leyendo a.mp4 y/o")]
    [InlineData("Leyendo D:/Clientes/Boda/a.mp4 and/or backup", "Leyendo a.mp4 and/or backup")]
    [InlineData("Leyendo D:/Clientes/Boda/a.mp4 y/u otro", "Leyendo a.mp4 y/u otro")]
    [InlineData("Leyendo D:/Clientes/Boda/a.mp4 listo, 3/4 hechos", "Leyendo a.mp4 listo, 3/4 hechos")]
    public void TrasUnArchivoConocido_UnConectorCortoSigueSiendoFrase(string line, string expected) =>
        Assert.Equal(expected, Ana.Scrub(line));

    [Theory]
    [InlineData("D:/C/Informe.log final/Secreto confidencial ")]
    [InlineData("D:/C/a.mp4 y/o b.mov c/d e/")]
    [InlineData("file:///D:/a.zip 3/4 b.mov x/")]
    public void ElCosteCreceEnProporcionAlTexto(string unit)
    {
        // Como en CrashReportScrubberSlashTests: el trabajo del analizador, no el reloj, al pasar de 1 KB a 8 KB por línea.
        string Repeat(int length) => string.Concat(Enumerable.Repeat(unit, (length / unit.Length) + 1))[..length];
        var scrubber = new CrashReportScrubber(AnaFolders) { Budget = long.MaxValue };

        scrubber.Scrub(Repeat(1024), out long small);
        scrubber.Scrub(Repeat(CrashReportScrubber.MaxLineLength), out long large);

        Assert.InRange(large, 1, 4L * CrashReportScrubber.MaxLineLength);
        Assert.InRange((double)large / small, 1, 8 * 1.25);
    }
}
