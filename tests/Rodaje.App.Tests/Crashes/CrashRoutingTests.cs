using System.Diagnostics;
using Rodaje.App.Services.Crashes;
using Rodaje.App.Tests.Support;

namespace Rodaje.App.Tests.Crashes;

/// <summary>
/// Tarea 111: la copia del registro de una sesión no se borra si su informe no se escribió, las capturas dejan una
/// línea en el registro, copiar el informe no lanza y el cableado de <c>Program</c> y <c>App</c> (orden de las vías,
/// errores del hilo de la interfaz) se prueba sin WinUI.
/// </summary>
public class CrashRoutingTests
{
    private const string Version = "1.0.0+prueba";

    [Fact]
    public void SesionSinCerrar_SiElInformeNoSeEscribe_ConservaSuRegistroYLoAnota()
    {
        using var dir = new TempDirectory();
        string session = CrashedSession(dir, "eligió escala");

        // Disco lleno o carpeta sin permiso: el informe no queda en disco. Como administrador (la CI de Linux), una
        // carpeta de solo lectura no lo impide, así que se simula con el escritor.
        var log = new SessionLog();
        var failing = new CrashReporter(dir.Path, Version, log) { ReportWriter = (_, _) => false };
        Assert.Empty(failing.RecoverUncleanSessions((_, _) => false));

        Assert.True(File.Exists(session), "la copia del registro era la única evidencia y se borró sin informe");
        Assert.Contains(log.Tail(), l => l.Contains("No se pudo escribir el informe de la sesión anterior", StringComparison.Ordinal));
        Assert.Empty(Directory.GetFiles(dir.Path, "*.txt"));

        // El siguiente arranque, con disco, sí la convierte en informe y entonces la borra.
        var next = new CrashReporter(dir.Path, Version, new SessionLog());
        string report = Assert.Single(next.RecoverUncleanSessions((_, _) => false));
        Assert.Contains("eligió escala", File.ReadAllText(report), StringComparison.Ordinal);
        Assert.False(File.Exists(session));
    }

    [Fact]
    public void SesionSinCerrar_QueNoSePuedeLeer_LoAnotaEnElRegistro()
    {
        using var dir = new TempDirectory();
        string session = CrashedSession(dir, "x");
        var log = new SessionLog();
        var reporter = new CrashReporter(dir.Path, Version, log);

        // Otra app la tiene abierta en exclusiva: leerla lanza IOException, que antes se tragaba sin rastro.
        using (new FileStream(session, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Empty(reporter.RecoverUncleanSessions((_, _) => false));
        }

        Assert.Contains(log.Tail(), l => l.Contains("No se pudo recuperar la sesión anterior", StringComparison.Ordinal));
        Assert.True(File.Exists(session));
    }

    [Fact]
    public void MarcarVistos_SiNoSePuede_LoAnotaEnElRegistro()
    {
        using var dir = new TempDirectory();
        // La carpeta de fallos es un archivo: no se puede crear ni escribir la marca.
        string folder = dir.File("fallos");
        File.WriteAllText(folder, "no es una carpeta");
        var log = new SessionLog();
        var reporter = new CrashReporter(folder, Version, log);

        reporter.AcknowledgePending();

        Assert.Contains(log.Tail(), l => l.Contains("No se pudieron marcar como vistos", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(ArgumentException))]
    [InlineData(typeof(NotSupportedException))]
    [InlineData(typeof(System.Runtime.InteropServices.COMException))]
    public void CopiarInforme_CualquierErrorDelPortapapeles_NoLanzaYLoAnota(Type error)
    {
        using var dir = new TempDirectory();
        string path = dir.File("informe.txt");
        File.WriteAllText(path, "texto del informe");
        var log = new SessionLog();

        bool copied = CrashRouting.TryCopyReport(path, _ => throw (Exception)Activator.CreateInstance(error, "portapapeles ocupado")!, log);

        Assert.False(copied);
        Assert.Contains(log.Tail(), l => l.Contains(error.Name, StringComparison.Ordinal));
    }

    [Fact]
    public void CopiarInforme_LlevaElTextoAlPortapapeles()
    {
        using var dir = new TempDirectory();
        string path = dir.File("informe.txt");
        File.WriteAllText(path, "texto del informe");
        string? clipboard = null;

        Assert.True(CrashRouting.TryCopyReport(path, text => clipboard = text, new SessionLog()));
        Assert.Equal("texto del informe", clipboard);
        Assert.False(CrashRouting.TryCopyReport(dir.File("no-existe.txt"), text => clipboard = text, new SessionLog()));
    }

    [Fact]
    public void Instalar_EnganchaLasViasAntesQueNada_YNoAbreLaSesion()
    {
        using var dir = new TempDirectory();
        var log = new SessionLog();
        var reporter = new CrashReporter(dir.Path, Version, log) { MinidumpWriter = (_, _) => false };
        var hooks = new RecordingHooks(log);
        var errorFile = new List<string?>();

        CrashRouting.Install(reporter, hooks, errorFile.Add, "rodaje.exe --prueba");

        // Las vías de excepción y el filtro nativo, antes de escribir nada en el registro.
        Assert.Equal(["unhandled:0", "unobserved:0", "native:0", "trace:0", "processExit:1", "later:1"], hooks.Calls);
        Assert.Same(reporter, hooks.Native);
        Assert.Contains("arranca: rodaje.exe --prueba", Assert.Single(log.Tail()), StringComparison.Ordinal);
        Assert.Equal(CrashRouting.GpuWarmUpDelay, hooks.Delay);
        // La copia en disco y la recuperación esperan a las opciones (BeginSession, después de AppOptions.Parse).
        Assert.Null(log.MirrorPath);
        Assert.False(Directory.Exists(Path.Combine(dir.Path, "sesiones")));

        // Cada vía enganchada llega al informe.
        var trace = Assert.IsType<SessionLogTraceListener>(hooks.Listener);
        trace.WriteLine("desde Trace");
        Assert.Contains(log.Tail(), l => l.EndsWith("desde Trace", StringComparison.Ordinal));

        var unobserved = new UnobservedTaskExceptionEventArgs(new AggregateException(new InvalidOperationException("tarea")));
        hooks.Unobserved!(null, unobserved);
        Assert.True(unobserved.Observed);

        hooks.Unhandled!(AppDomain.CurrentDomain, new UnhandledExceptionEventArgs(new InvalidOperationException("hilo"), isTerminating: true));
        Assert.Contains("hilo", Assert.Single(errorFile), StringComparison.Ordinal);
        string fatal = Assert.Single(reporter.PendingReports());
        Assert.Contains("AppDomain.UnhandledException", File.ReadAllText(fatal), StringComparison.Ordinal);
    }

    [Fact]
    public void Instalar_AlSalirElProceso_CierraLaSesion()
    {
        using var dir = new TempDirectory();
        var log = new SessionLog();
        var reporter = new CrashReporter(dir.Path, Version, log);
        var hooks = new RecordingHooks(log);
        CrashRouting.Install(reporter, hooks, _ => { }, "rodaje.exe");
        _ = reporter.BeginSession(automated: false);
        string mirror = log.MirrorPath!;
        Assert.True(File.Exists(mirror));

        hooks.ProcessExit!(null, EventArgs.Empty);

        Assert.False(File.Exists(mirror));
    }

    [Fact]
    public void HiloDeLaInterfaz_ConVentana_InformeRecuperadoAvisoUnaVezPorRafagaYSigue()
    {
        using var dir = new TempDirectory();
        var reporter = new CrashReporter(dir.Path, Version, new SessionLog());
        var shown = new List<string>();
        var context = new GuardedSynchronizationContext(new InlineContext(),
            ex => CrashRouting.OnDispatcherException(ex, shown.Add, reporter.Write));

        for (int i = 0; i < 3; i++)
        {
            context.Post(_ => throw new InvalidOperationException("falla en cada tick"), null);
        }

        Assert.Equal(["falla en cada tick"], shown);
        string report = Assert.Single(Directory.GetFiles(dir.Path, "*.txt"));
        Assert.EndsWith("-recuperado.txt", report, StringComparison.Ordinal);
        Assert.Contains("DispatcherQueue", File.ReadAllText(report), StringComparison.Ordinal);
        Assert.Empty(reporter.PendingReports());
    }

    [Fact]
    public void HiloDeLaInterfaz_SinVentana_InformeFatalYSigueSuCamino()
    {
        using var dir = new TempDirectory();
        var reporter = new CrashReporter(dir.Path, Version, new SessionLog());
        var context = new GuardedSynchronizationContext(new InlineContext(),
            ex => CrashRouting.OnDispatcherException(ex, null, reporter.Write));

        Assert.Throws<InvalidOperationException>(() => context.Post(_ => throw new InvalidOperationException("antes de la ventana"), null));

        string report = Assert.Single(reporter.PendingReports());
        Assert.Contains("antes de la ventana", File.ReadAllText(report), StringComparison.Ordinal);
    }

    [Fact]
    public void Xaml_SinExcepcion_UsaElMensaje()
    {
        using var dir = new TempDirectory();
        var reporter = new CrashReporter(dir.Path, Version, new SessionLog());
        var shown = new List<string>();

        bool handled = CrashRouting.OnXamlUnhandled(null, "error de XAML sin excepción", shown.Add, reporter.Write, reporter.Write);

        Assert.True(handled);
        Assert.Equal(["error de XAML sin excepción"], shown);
        Assert.Contains("error de XAML sin excepción", File.ReadAllText(Assert.Single(Directory.GetFiles(dir.Path, "*.txt"))), StringComparison.Ordinal);
    }

    // Una sesión de un proceso que terminó sin cerrarse: su copia del registro queda en sesiones\.
    private static string CrashedSession(TempDirectory dir, string line)
    {
        var crashedLog = new SessionLog();
        var crashed = new CrashReporter(dir.Path, Version, crashedLog);
        crashed.StartSession(processId: 4242, processStartTicks: 1234);
        crashedLog.Append(line);
        string session = crashedLog.MirrorPath!;
        crashedLog.Dispose();
        return session;
    }

    /// <summary>Anota cada enganche y cuántas líneas tenía el registro en ese momento, sin tocar el proceso de pruebas.</summary>
    private sealed class RecordingHooks(SessionLog log) : CrashHooks
    {
        public List<string> Calls { get; } = [];

        public UnhandledExceptionEventHandler? Unhandled { get; private set; }

        public EventHandler<UnobservedTaskExceptionEventArgs>? Unobserved { get; private set; }

        public CrashReporter? Native { get; private set; }

        public TraceListener? Listener { get; private set; }

        public EventHandler? ProcessExit { get; private set; }

        public TimeSpan Delay { get; private set; }

        public override void OnUnhandled(UnhandledExceptionEventHandler handler)
        {
            Record("unhandled");
            Unhandled = handler;
        }

        public override void OnUnobservedTask(EventHandler<UnobservedTaskExceptionEventArgs> handler)
        {
            Record("unobserved");
            Unobserved = handler;
        }

        public override void InstallNative(CrashReporter crashes)
        {
            Record("native");
            Native = crashes;
        }

        public override void AddTraceListener(TraceListener listener)
        {
            Record("trace");
            Listener = listener;
        }

        public override void OnProcessExit(EventHandler handler)
        {
            Record("processExit");
            ProcessExit = handler;
        }

        public override void RunLater(TimeSpan delay, Action action)
        {
            Record("later");
            Delay = delay;
        }

        private void Record(string name) => Calls.Add(FormattableString.Invariant($"{name}:{log.Tail().Count}"));
    }

    private sealed class InlineContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => d(state);
    }
}
