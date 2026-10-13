using System.Globalization;
using System.Runtime.CompilerServices;
using Rodaje.App.Services.Crashes;
using Rodaje.App.Tests.Support;

namespace Rodaje.App.Tests.Crashes;

/// <summary>
/// Tarea 107: un error recuperado que se repite en cada tick deja un informe por firma cada 10 s, con «repetido N veces»,
/// y una ráfaga no borra el fatal que espera su aviso.
/// </summary>
public class CrashBurstTests
{
    private static readonly DateTime Start = new(2026, 10, 13, 10, 0, 0, DateTimeKind.Local);

    [Fact]
    public void Rafaga_LaMismaFirma_DejaUnInformeCada10s_YCuentaLasRepeticiones()
    {
        using var dir = new TempDirectory();
        DateTime now = Start;
        var reporter = new CrashReporter(dir.Path, "1.0.0+prueba", new SessionLog(), clock: () => now);
        var repeats = new List<bool>();

        string? first = null;
        for (int tick = 0; tick < 50; tick++)
        {
            now = Start.AddMilliseconds(100 * tick);
            string? path = reporter.Write(CrashKind.DispatcherCallback, ThrownEachTick(), fatal: false, out bool repeated);
            first ??= path;
            Assert.Equal(first, path);
            repeats.Add(repeated);
        }

        Assert.False(repeats[0]);
        Assert.All(repeats.Skip(1), Assert.True);
        Assert.Single(Reports(dir));

        now = Start.AddSeconds(11);
        string? second = reporter.Write(CrashKind.DispatcherCallback, ThrownEachTick(), fatal: false, out bool again);

        Assert.False(again);
        Assert.NotEqual(first, second);
        Assert.Equal(2, Reports(dir).Length);
        Assert.Contains("Repetido 49 veces más hasta las 10:00:04.900", File.ReadAllText(first!), StringComparison.Ordinal);
        Assert.DoesNotContain("Repetido", File.ReadAllText(second!), StringComparison.Ordinal);
    }

    [Fact]
    public void Rafaga_SinInformeNuevo_AlCerrarLaSesionAnotaLasRepeticiones()
    {
        using var dir = new TempDirectory();
        DateTime now = Start;
        var reporter = new CrashReporter(dir.Path, "1.0.0+prueba", new SessionLog(), clock: () => now);
        string? path = reporter.Write(CrashKind.Caught, ThrownEachTick().ToString(), fatal: false, out _);
        now = now.AddSeconds(1);
        reporter.Write(CrashKind.Caught, ThrownEachTick().ToString(), fatal: false, out bool repeated);

        reporter.EndSession();
        reporter.EndSession();

        Assert.True(repeated);
        string text = File.ReadAllText(path!);
        Assert.Contains("Repetido 1 veces más", text, StringComparison.Ordinal);
        Assert.Equal(2, text.Split("Repetido").Length);
    }

    [Fact]
    public void FirmasDistintas_YFatales_NoSeAgrupan()
    {
        using var dir = new TempDirectory();
        var reporter = new CrashReporter(dir.Path, "1.0.0+prueba", new SessionLog(), clock: () => Start);

        reporter.Write(CrashKind.DispatcherCallback, ThrownEachTick(), fatal: false, out bool a);
        reporter.Write(CrashKind.DispatcherCallback, ThrownElsewhere(), fatal: false, out bool b);
        reporter.Write(CrashKind.AppDomainUnhandled, ThrownEachTick(), fatal: true, out bool c);

        Assert.False(a);
        Assert.False(b);
        Assert.False(c);
        Assert.Equal(3, Reports(dir).Length);
    }

    [Fact]
    public void LaMismaExcepcionYaInformada_CuentaComoRepetida()
    {
        using var dir = new TempDirectory();
        var reporter = new CrashReporter(dir.Path, "1.0.0+prueba", new SessionLog());
        Exception ex = ThrownEachTick();

        reporter.Write(CrashKind.DispatcherCallback, ex, fatal: false, out bool first);
        reporter.Write(CrashKind.XamlUnhandled, ex, fatal: false, out bool second);

        Assert.False(first);
        Assert.True(second);
    }

    [Fact]
    public void Firma_EsElTipoYLaPrimeraLineaDeLaPila()
    {
        string a = CrashReporter.Signature(ThrownEachTick().ToString());
        string b = CrashReporter.Signature(new InvalidOperationException("otro mensaje distinto").ToString() + Environment.NewLine + ThrownEachTick().StackTrace);

        Assert.StartsWith("System.InvalidOperationException\nat ", a, StringComparison.Ordinal);
        Assert.Contains(nameof(Throw), a, StringComparison.Ordinal);
        Assert.Equal(a, b);
        Assert.Equal("autoprueba: algo", CrashReporter.Signature("autoprueba: algo"));
        Assert.NotEqual(CrashReporter.Signature("autoprueba: algo"), CrashReporter.Signature("autoprueba: otra cosa"));
    }

    [Fact]
    public void CienRecuperados_NoBorranElFatalPendiente()
    {
        using var dir = new TempDirectory();
        DateTime now = Start;
        var reporter = new CrashReporter(dir.Path, "1.0.0+prueba", new SessionLog(), clock: () => now = now.AddMilliseconds(10));
        string? fatal = reporter.Write(CrashKind.AppDomainUnhandled, ThrownEachTick(), fatal: true);

        for (int i = 0; i < 100; i++)
        {
            reporter.Write(CrashKind.Caught, "error " + i.ToString(CultureInfo.InvariantCulture), fatal: false);
        }

        Assert.True(File.Exists(fatal));
        Assert.Equal(new[] { fatal! }, reporter.PendingReports());
        Assert.Equal(CrashReporter.KeepReports, Reports(dir).Count(f => f.EndsWith("-recuperado.txt", StringComparison.Ordinal)));
    }

    [Fact]
    public void SeConservanLosUltimosFatales()
    {
        using var dir = new TempDirectory();
        DateTime now = Start;
        var reporter = new CrashReporter(dir.Path, "1.0.0+prueba", new SessionLog(), clock: () => now = now.AddSeconds(1));
        reporter.Write(CrashKind.Caught, "recuperado", fatal: false);

        for (int i = 0; i < CrashReporter.KeepReports + 5; i++)
        {
            reporter.Write(CrashKind.Startup, "fatal " + i.ToString(CultureInfo.InvariantCulture), fatal: true);
        }

        Assert.Equal(CrashReporter.KeepReports + 1, Reports(dir).Length);
        Assert.Single(Reports(dir), f => f.EndsWith("-recuperado.txt", StringComparison.Ordinal));
    }

    private static string[] Reports(TempDirectory dir) => Directory.GetFiles(dir.Path, "*.txt");

    // Una excepción nueva en cada llamada, lanzada siempre desde el mismo sitio (como un tick que falla).
    private static InvalidOperationException ThrownEachTick()
    {
        try
        {
            Throw("falló el tick " + Guid.NewGuid().ToString("N"));
            return new InvalidOperationException("no llegó");
        }
        catch (InvalidOperationException ex)
        {
            return ex;
        }
    }

    private static InvalidOperationException ThrownElsewhere()
    {
        try
        {
            ThrowElsewhere();
            return new InvalidOperationException("no llegó");
        }
        catch (InvalidOperationException ex)
        {
            return ex;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Throw(string message) => throw new InvalidOperationException(message);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowElsewhere() => throw new InvalidOperationException("falló en otro sitio");
}
