using System.Globalization;
using Rodaje.App.Services.Crashes;
using Rodaje.App.Tests.Support;

namespace Rodaje.App.Tests.Crashes;

/// <summary>
/// Tarea 187: desde #159 la firma conservaba cualquier palabra «0x…», así que un mensaje con una dirección o un handle que
/// cambia en cada repetición daba una firma nueva cada vez y esquivaba los topes y el aviso. Solo los códigos de 32 bits
/// (hasta 8 cifras hexadecimales, o 10 decimales tras «código») cuentan para la firma. Con el reloj inyectado: sin esperas.
/// </summary>
public class CrashSignaturePointerTests
{
    private static readonly DateTime Start = new(2026, 10, 13, 10, 0, 0, DateTimeKind.Local);

    [Fact]
    public void Firma_PunterosDistintosDe16Cifras_DanLaMismaFirma()
    {
        Assert.Equal(
            CrashReporter.Signature("Violación de acceso en 0x000001F2A3B4C5D6"),
            CrashReporter.Signature("Violación de acceso en 0x000001F2A3B4C5E8"));
        Assert.Equal(
            CrashReporter.Signature("handle -0x00007FFA12345678 inválido"),
            CrashReporter.Signature("handle -0x00007FFA9ABCDEF0 inválido"));

        // Tras «código» o «HRESULT», un número o un «0x…» de 64 bits tampoco es un código de error.
        Assert.Equal(CrashReporter.Signature("código 0x000001F2A3B4C5D6"), CrashReporter.Signature("código 0x000001F2A3B4C5E8"));
        Assert.Equal(CrashReporter.Signature("HRESULT 2139062143123456"), CrashReporter.Signature("HRESULT 2139062143654321"));
    }

    [Fact]
    public void Firma_LosCodigosDe32Bits_SiguenDandoFirmasDistintas()
    {
        Assert.NotEqual(CrashReporter.Signature("COMException (0x887A0005)"), CrashReporter.Signature("COMException (0x80070057)"));
        Assert.NotEqual(CrashReporter.Signature("Excepción 0xC0000005 en 0x000001F2A3B4C5D6"), CrashReporter.Signature("Excepción 0xC000027B en 0x000001F2A3B4C5D6"));
        Assert.NotEqual(CrashReporter.Signature("Falló con código -2147024809"), CrashReporter.Signature("Falló con código -2147467259"));
        Assert.Contains("0x887A0005", CrashReporter.Signature("COMException (0x887A0005) en 0x000001F2A3B4C5D6"), StringComparison.Ordinal);
    }

    [Fact]
    public void PunterosDistintosEnCadaRepeticion_ElTopePorFirmaActua()
    {
        using var dir = new TempDirectory();
        DateTime now = Start;
        var reporter = new CrashReporter(dir.Path, "1.0.0+prueba", new SessionLog(), clock: () => now);
        var written = new HashSet<string>(StringComparer.Ordinal);
        int notices = 0;

        // Un error cada 11 s (cada uno cierra la ráfaga) con una dirección distinta cada vez.
        for (int i = 0; i < 100; i++)
        {
            now = Start.AddSeconds(11 * i);
            string pointer = (0x000001F2A3B4C5D6L + (i * 0x40L)).ToString("X16", CultureInfo.InvariantCulture);
            string? path = reporter.Write(CrashKind.Caught, "Violación de acceso al leer 0x" + pointer, fatal: false, out bool repeated);
            written.Add(path!);
            if (!repeated)
            {
                notices++;
            }
        }

        reporter.EndSession();

        Assert.Equal(CrashReporter.MaxReportsPerSignature, written.Count);
        Assert.Equal(CrashReporter.MaxReportsPerSignature, notices);
        Assert.Contains("Repetido 90 veces más", File.ReadAllText(written.Order(StringComparer.Ordinal).Last()), StringComparison.Ordinal);
    }

    [Fact]
    public void PunterosDistintosEnUnaRafaga_DejanUnSoloInforme()
    {
        using var dir = new TempDirectory();
        DateTime now = Start;
        var reporter = new CrashReporter(dir.Path, "1.0.0+prueba", new SessionLog(), clock: () => now);

        string? first = reporter.Write(CrashKind.Caught, "Violación de acceso al leer 0x000001F2A3B4C5D6", fatal: false, out bool a);
        now = now.AddMilliseconds(100);
        string? second = reporter.Write(CrashKind.Caught, "Violación de acceso al leer 0x000001F2A3B4C5E8", fatal: false, out bool b);

        Assert.False(a);
        Assert.True(b);
        Assert.Equal(first, second);
    }
}
