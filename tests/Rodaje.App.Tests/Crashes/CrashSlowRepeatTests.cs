using System.Runtime.CompilerServices;
using Rodaje.App.Services.Crashes;
using Rodaje.App.Tests.Support;

namespace Rodaje.App.Tests.Crashes;

/// <summary>
/// Tarea 155: un error que se repite con pausas más largas que <see cref="CrashReporter.BurstWindow"/> esquivaba el tope
/// de ráfaga (un informe y un aviso por repetición), los códigos de error se fundían en la firma y un archivo bloqueado
/// detenía el recorte. Con el reloj inyectado del <see cref="CrashReporter"/>: ninguna prueba duerme.
/// </summary>
public class CrashSlowRepeatTests
{
    private static readonly DateTime Start = new(2026, 10, 13, 10, 0, 0, DateTimeKind.Local);

    [Fact]
    public void ErrorCada11s_DejaComoMuchoDiezInformesYDiezAvisosPorHora_YCuentaElRestoEnElUltimo()
    {
        using var dir = new TempDirectory();
        DateTime now = Start;
        var reporter = new CrashReporter(dir.Path, "1.0.0+prueba", new SessionLog(), clock: () => now);
        var written = new HashSet<string>(StringComparer.Ordinal);
        int notices = 0;

        // Como App: un callback que lanza cada 11 s, con ventana; avisa solo si no es una repetición.
        for (int i = 0; i < 100; i++)
        {
            now = Start.AddSeconds(11 * i);
            _ = CrashRouting.OnDispatcherException(Thrown("falló el autoguardado"), _ => notices++, Report);
        }

        Assert.Equal(CrashReporter.MaxReportsPerSignature, written.Count);
        Assert.Equal(CrashReporter.MaxReportsPerSignature, notices);
        Assert.Equal(CrashReporter.MaxReportsPerSignature, Directory.GetFiles(dir.Path, "*.txt").Length);

        reporter.EndSession();
        string last = written.Order(StringComparer.Ordinal).Last();
        Assert.Contains("Repetido 90 veces más hasta las 10:18:09.000", File.ReadAllText(last), StringComparison.Ordinal);

        // Una hora después del primero de los diez, la firma vuelve a tener informe propio y aviso.
        now = Start.AddHours(1);
        _ = CrashRouting.OnDispatcherException(Thrown("falló el autoguardado"), _ => notices++, Report);
        Assert.Equal(CrashReporter.MaxReportsPerSignature + 1, notices);

        string? Report(CrashKind kind, Exception exception, bool fatal, out bool repeated)
        {
            string? path = reporter.Write(kind, exception, fatal, out repeated);
            written.Add(path!);
            return path;
        }
    }

    [Fact]
    public void TopePorFirma_AunqueSuRafagaSeOlvide_SigueContandoEnElUltimo()
    {
        using var dir = new TempDirectory();
        DateTime now = Start;
        // Sin recorte: las 70 firmas nuevas se llevarían el informe del autoguardado, y aquí solo importa la ráfaga.
        var reporter = new CrashReporter(dir.Path, "1.0.0+prueba", new SessionLog(), clock: () => now) { PruneDeleter = _ => { } };
        string? last = null;
        for (int i = 0; i < CrashReporter.MaxReportsPerSignature; i++)
        {
            now = Start.AddSeconds(11 * i);
            last = reporter.Write(CrashKind.Caught, "falló el autoguardado", fatal: false);
        }

        // 64 firmas distintas a la vez: la ráfaga del autoguardado se olvida (MaxBursts).
        now = now.AddSeconds(11);
        for (int i = 0; i < 70; i++)
        {
            _ = reporter.Write(CrashKind.Caught, "otro error " + new string((char)('a' + (i / 26)), 1) + (char)('a' + (i % 26)), fatal: false);
        }

        now = now.AddSeconds(11);
        string? again = reporter.Write(CrashKind.Caught, "falló el autoguardado", fatal: false, out bool repeated);
        reporter.EndSession();

        Assert.True(repeated);
        Assert.Equal(last, again);
        Assert.Contains("Repetido 1 veces más", File.ReadAllText(last!), StringComparison.Ordinal);
    }

    [Fact]
    public void Firma_ConservaLosCodigosDeError()
    {
        string a = CrashReporter.Signature(Thrown("Error HRESULT E_FAIL (0x887A0005)").ToString());
        string b = CrashReporter.Signature(Thrown("Error HRESULT E_FAIL (0x80070057)").ToString());

        Assert.NotEqual(a, b);
        Assert.Contains("0x887A0005", a, StringComparison.Ordinal);
        Assert.NotEqual(CrashReporter.Signature("ffmpeg terminó con exit code 1"), CrashReporter.Signature("ffmpeg terminó con exit code 137"));
        Assert.NotEqual(CrashReporter.Signature("Falló con código -2147024809"), CrashReporter.Signature("Falló con código -2147467259"));
        Assert.NotEqual(CrashReporter.Signature("HRESULT: -2147024809"), CrashReporter.Signature("HRESULT: 5"));
        // Los demás números siguen sin contar: el cuadro, y lo que no sigue directamente a «código».
        Assert.Equal(CrashReporter.Signature("cuadro 120, código 5"), CrashReporter.Signature("cuadro 9081, código 5"));
        Assert.Equal("código 5 y #", CrashReporter.Signature("código 5 y 7"));
    }

    [Fact]
    public void ComExceptionConHresultDistintos_YLaMismaPila_SonDosInformesConSuAviso()
    {
        using var dir = new TempDirectory();
        var reporter = new CrashReporter(dir.Path, "1.0.0+prueba", new SessionLog(), clock: () => Start);

        string? first = reporter.Write(CrashKind.DispatcherCallback, Thrown("Error HRESULT E_FAIL has been returned (0x887A0005)"), fatal: false, out bool a);
        string? second = reporter.Write(CrashKind.DispatcherCallback, Thrown("Error HRESULT E_FAIL has been returned (0x80070057)"), fatal: false, out bool b);

        Assert.False(a);
        Assert.False(b);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Repeticiones_ListanHastaCincoPrimerasLineasDistintas()
    {
        using var dir = new TempDirectory();
        DateTime now = Start;
        var reporter = new CrashReporter(dir.Path, "1.0.0+prueba", new SessionLog(), clock: () => now);
        string? path = reporter.Write(CrashKind.Caught, "no se pudo leer el cuadro 1", fatal: false);
        for (int i = 0; i < 9; i++)
        {
            now = now.AddMilliseconds(100);
            // 1, 2, …, 9 y la del informe otra vez: ocho distintas de la del informe, se listan cinco.
            _ = reporter.Write(CrashKind.Caught, "no se pudo leer el cuadro " + (i % 8 + 2).ToString(System.Globalization.CultureInfo.InvariantCulture), fatal: false);
        }

        now = now.AddMilliseconds(100);
        _ = reporter.Write(CrashKind.Caught, "no se pudo leer el cuadro 1", fatal: false);
        reporter.EndSession();

        string text = File.ReadAllText(path!);
        Assert.Contains("Repetido 10 veces más", text, StringComparison.Ordinal);
        Assert.Contains("Primeras líneas distintas de las repeticiones (hasta 5):", text, StringComparison.Ordinal);
        string[] listed = [.. text.ReplaceLineEndings("\n").Split('\n').Where(l => l.StartsWith("  no se pudo leer el cuadro ", StringComparison.Ordinal))];
        Assert.Equal(["  no se pudo leer el cuadro 2", "  no se pudo leer el cuadro 3", "  no se pudo leer el cuadro 4", "  no se pudo leer el cuadro 5", "  no se pudo leer el cuadro 6"], listed);
    }

    [Fact]
    public void Recorte_UnArchivoBloqueado_NoDejaSinBorrarALosDemas()
    {
        using var dir = new TempDirectory();
        DateTime now = Start;
        string? locked = null;
        var reporter = new CrashReporter(dir.Path, "1.0.0+prueba", new SessionLog(), clock: () => now = now.AddSeconds(1))
        {
            PruneDeleter = path =>
            {
                if (path == locked)
                {
                    throw new IOException("El proceso no tiene acceso al archivo porque está siendo utilizado por otro proceso.");
                }

                File.Delete(path);
            },
        };

        for (int i = 0; i < CrashReporter.KeepReports; i++)
        {
            string? path = reporter.Write(CrashKind.Startup, "fatal " + (char)('a' + (i / 26)) + (char)('a' + (i % 26)), fatal: true);
            locked ??= path;
        }

        // Cinco fatales más: hay que borrar los cinco más viejos y el primero está bloqueado.
        for (int i = 0; i < 5; i++)
        {
            _ = reporter.Write(CrashKind.Startup, "otro fatal " + (char)('a' + i), fatal: true);
        }

        string[] left = Directory.GetFiles(dir.Path, "*.txt");
        Assert.Contains(locked!, left);
        Assert.Equal(CrashReporter.KeepReports + 1, left.Length);
    }

    private static InvalidOperationException Thrown(string message)
    {
        try
        {
            Throw(message);
            return new InvalidOperationException("no llegó");
        }
        catch (InvalidOperationException ex)
        {
            return ex;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Throw(string message) => throw new InvalidOperationException(message);
}
