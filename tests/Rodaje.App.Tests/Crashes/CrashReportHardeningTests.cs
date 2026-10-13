using System.Runtime.CompilerServices;
using Rodaje.App.Services.Crashes;
using Rodaje.App.Tests.Support;

namespace Rodaje.App.Tests.Crashes;

/// <summary>
/// Informes huérfanos de una ráfaga, firma que ignoraba el mensaje, tope de informes por ráfaga, minivolcado
/// a medias, cierre de sesión que esperaba sin fin y el enganche de <c>WM_ENDSESSION</c> que no se reintentaba. Con el
/// reloj inyectado del <see cref="CrashReporter"/>: ninguna prueba duerme.
/// </summary>
public class CrashReportHardeningTests
{
    private static readonly DateTime Start = new(2026, 10, 13, 10, 0, 0, DateTimeKind.Local);

    [Fact]
    public void Rafaga_SiYaSeBorroSuInforme_NoQuedaUnArchivoConSoloLaRepeticion()
    {
        using var dir = new TempDirectory();
        DateTime now = Start;
        var reporter = new CrashReporter(dir.Path, "1.0.0+prueba", new SessionLog(), clock: () => now);
        string? path = reporter.Write(CrashKind.Caught, Thrown("falló el tick").ToString(), fatal: false, out _);
        now = now.AddSeconds(1);
        reporter.Write(CrashKind.Caught, Thrown("falló el tick").ToString(), fatal: false, out bool repeated);

        // Prune (30 recuperados más nuevos) se lo llevó mientras la ráfaga seguía abierta.
        File.Delete(path!);
        reporter.EndSession();

        Assert.True(repeated);
        Assert.False(File.Exists(path));
        Assert.Empty(Directory.GetFiles(dir.Path, "*.txt"));
    }

    [Fact]
    public void MismaPila_ConMensajesDistintos_CadaUnoTieneSuInformeYSuAviso()
    {
        using var dir = new TempDirectory();
        var reporter = new CrashReporter(dir.Path, "1.0.0+prueba", new SessionLog(), clock: () => Start);

        string? first = reporter.Write(CrashKind.DispatcherCallback, Thrown("no se encontró el clip"), fatal: false, out bool a);
        string? second = reporter.Write(CrashKind.DispatcherCallback, Thrown("el archivo está bloqueado"), fatal: false, out bool b);

        Assert.False(a);
        Assert.False(b); // Quien llama avisa en la ventana y escribe rodaje-error.txt.
        Assert.NotEqual(first, second);
        Assert.Contains("el archivo está bloqueado", File.ReadAllText(second!), StringComparison.Ordinal);
    }

    [Fact]
    public void MismaPila_ConMensajesQueSoloCambianEnNumeros_EsUnaRepeticion()
    {
        using var dir = new TempDirectory();
        var reporter = new CrashReporter(dir.Path, "1.0.0+prueba", new SessionLog(), clock: () => Start);

        reporter.Write(CrashKind.DispatcherCallback, Thrown("cuadro 120 fuera de rango (pista v2)"), fatal: false, out bool a);
        reporter.Write(CrashKind.DispatcherCallback, Thrown("cuadro 9081 fuera de rango (pista v31)"), fatal: false, out bool b);

        Assert.False(a);
        Assert.True(b);
        Assert.Single(Directory.GetFiles(dir.Path, "*.txt"));
    }

    [Fact]
    public void RafagaQueNoPara_DejaComoMuchoTresInformes_YCuentaElRestoEnElUltimo()
    {
        using var dir = new TempDirectory();
        DateTime now = Start;
        var reporter = new CrashReporter(dir.Path, "1.0.0+prueba", new SessionLog(), clock: () => now);
        var written = new HashSet<string>(StringComparer.Ordinal);

        // Un error cada 100 ms durante un minuto: sin tope, seis informes (uno cada 10 s); a los 5 min, los 30.
        for (int tick = 0; tick < 600; tick++)
        {
            now = Start.AddMilliseconds(100 * tick);
            string? path = reporter.Write(CrashKind.DispatcherCallback, Thrown("falló el tick"), fatal: false, out _);
            written.Add(path!);
        }

        Assert.Equal(CrashReporter.MaxReportsPerBurst, written.Count);
        Assert.Equal(CrashReporter.MaxReportsPerBurst, Directory.GetFiles(dir.Path, "*.txt").Length);

        // Tras una pausa más larga que el intervalo, el error vuelve a tener informe propio (y aviso).
        now = Start.AddSeconds(80);
        string? again = reporter.Write(CrashKind.DispatcherCallback, Thrown("falló el tick"), fatal: false, out bool repeated);

        Assert.False(repeated);
        Assert.DoesNotContain(again!, written);
        string last = written.Order(StringComparer.Ordinal).Last();
        Assert.Contains("Repetido 399 veces más hasta las 10:00:59.900", File.ReadAllText(last), StringComparison.Ordinal);
    }

    [Fact]
    public unsafe void FalloNativo_ElMinivolcadoSeEscribeAparte_YSoloSeLlamaDmpCuandoTermino()
    {
        using var dir = new TempDirectory();
        string? writtenTo = null;
        string[]? dumpsWhileWriting = null;
        var reporter = new CrashReporter(dir.Path, "1.0.0+prueba", new SessionLog())
        {
            MinidumpWriter = (path, _) =>
            {
                writtenTo = path;
                File.WriteAllBytes(path, "MDMP"u8.ToArray()); // Si el vigilante termina el proceso aquí…
                dumpsWhileWriting = Directory.GetFiles(dir.Path, "*.dmp");
                return 0;
            },
        };

        string? report = reporter.WriteNativeFatal(0xC0000005, null);

        Assert.Empty(dumpsWhileWriting!); // …no queda un .dmp truncado que Prune cuente como uno de los 3.
        Assert.EndsWith(".dmp.tmp", writtenTo, StringComparison.Ordinal);
        Assert.Equal(Path.ChangeExtension(report, ".dmp"), Assert.Single(Directory.GetFiles(dir.Path, "*.dmp")));
        Assert.False(File.Exists(writtenTo));
    }

    [Fact]
    public unsafe void FalloNativo_BorraLosMinivolcadosAMediasDeOtraSesion()
    {
        using var dir = new TempDirectory();
        string leftover = dir.File("2026-10-12_09-00-00-000.dmp.tmp");
        File.WriteAllBytes(leftover, "MD"u8.ToArray());
        var reporter = new CrashReporter(dir.Path, "1.0.0+prueba", new SessionLog())
        {
            MinidumpWriter = (path, _) =>
            {
                File.WriteAllBytes(path, "MDMP"u8.ToArray());
                return 0;
            },
        };

        Assert.NotNull(reporter.WriteNativeFatal(0xC0000005, null));

        Assert.False(File.Exists(leftover));
        Assert.Single(Directory.GetFiles(dir.Path, "*.dmp"));
    }

    [Fact]
    public async Task EndSession_ConUnInformeAMediasEnOtroHilo_NoEsperaSinFin()
    {
        using var dir = new TempDirectory();
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var log = new SessionLog();
        var reporter = new CrashReporter(dir.Path, "1.0.0+prueba", log)
        {
            GateTimeout = TimeSpan.FromMilliseconds(50),
            ReportWriter = (_, _) =>
            {
                entered.Set();
                _ = release.Wait(TimeSpan.FromSeconds(60)); // Un disco trabado a mitad del informe.
                return false;
            },
        };
        reporter.StartSession(processId: 11, processStartTicks: 11);
        string mirror = log.MirrorPath!;
        Task stuck = Task.Run(() => reporter.Write(CrashKind.Caught, "algo", fatal: false));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(30)));

        try
        {
            // WM_ENDSESSION llega en el hilo de la interfaz: con lock sin plazo, esperaba al otro hilo (aquí 60 s).
            await Task.Run(reporter.EndSession).WaitAsync(TimeSpan.FromSeconds(30));
            Assert.False(File.Exists(mirror));
        }
        finally
        {
            release.Set();
            await stuck.WaitAsync(TimeSpan.FromSeconds(30));
        }
    }

    [Fact]
    public void SessionEndWatcher_SiElEngancheFalla_SePuedeReintentar()
    {
        Action? slot = null;
        Action onEnd = () => { };

        Assert.False(SessionEndWatcher.Install(ref slot, onEnd, () => false));
        Assert.Null(slot);
        Assert.True(SessionEndWatcher.Install(ref slot, onEnd, () => true));
        Assert.Same(onEnd, slot);
        Assert.False(SessionEndWatcher.Install(ref slot, () => { }, () => true));
    }

    // Una excepción lanzada siempre desde el mismo sitio, con el mensaje que se pida.
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
