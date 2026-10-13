using System.Globalization;
using System.Runtime.CompilerServices;
using Rodaje.App.Services.Crashes;
using Rodaje.App.Tests.Support;

namespace Rodaje.App.Tests.Crashes;

/// <summary>Ningún cierre sin rastro: cada vía de excepción deja su informe en la carpeta de fallos.</summary>
public class CrashReporterTests
{
    private const string Version = "1.0.0+abc123def456";
    private const string Gpu = "GPU de prueba (controlador 1.2.3)";

    [Fact]
    public void WinUI_Atendida_DejaUnInformeRecuperadoConPilaVersionGpuYRegistro()
    {
        using var dir = new TempDirectory();
        CrashReporter reporter = NewReporter(dir, out SessionLog log);
        log.Append("abrió el proyecto");
        var shown = new List<string>();

        // El camino de App.OnUnhandledException con ventana.
        bool handled = CrashRouting.OnXamlUnhandled(Thrown(), "mensaje de WinUI", shown.Add, reporter.Write, reporter.Write);

        Assert.True(handled);
        Assert.Equal(["falló el manejador de prueba"], shown);
        string path = Assert.Single(Reports(dir));
        Assert.EndsWith("-recuperado.txt", path, StringComparison.Ordinal);
        string text = File.ReadAllText(path);
        Assert.Contains("Application.UnhandledException", text, StringComparison.Ordinal);
        Assert.Contains("La app se cerró: no", text, StringComparison.Ordinal);
        Assert.Contains(nameof(ThrowingMethod), text, StringComparison.Ordinal);
        Assert.Contains("Versión: " + Version, text, StringComparison.Ordinal);
        Assert.Contains("GPU: " + Gpu, text, StringComparison.Ordinal);
        Assert.Contains("abrió el proyecto", text, StringComparison.Ordinal);
        Assert.Empty(reporter.PendingReports());
    }

    [Fact]
    public void WinUI_SinVentana_DejaUnInformeFatalPendiente()
    {
        using var dir = new TempDirectory();
        CrashReporter reporter = NewReporter(dir, out _);

        // El camino de App.OnUnhandledException antes de que haya ventana: no se atiende (WinUI cierra la app).
        bool handled = CrashRouting.OnXamlUnhandled(Thrown(), "mensaje de WinUI", null, reporter.Write, reporter.Write);

        Assert.False(handled);
        string path = Assert.Single(Reports(dir));
        Assert.False(Path.GetFileNameWithoutExtension(path).EndsWith("-recuperado", StringComparison.Ordinal));
        Assert.Contains("La app se cerró: sí", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.Equal(new[] { path }, reporter.PendingReports());
    }

    [Fact]
    public void AppDomain_DejaSuInforme()
    {
        using var dir = new TempDirectory();
        CrashReporter reporter = NewReporter(dir, out _);

        reporter.OnAppDomainUnhandled(null, new UnhandledExceptionEventArgs(Thrown(), isTerminating: true));

        string report = Assert.Single(reporter.PendingReports());
        string text = File.ReadAllText(report);
        Assert.Contains("AppDomain.UnhandledException", text, StringComparison.Ordinal);
        Assert.Contains(nameof(ThrowingMethod), text, StringComparison.Ordinal);
    }

    [Fact]
    public void TareaNoObservada_DejaSuInformeYQuedaObservada()
    {
        using var dir = new TempDirectory();
        CrashReporter reporter = NewReporter(dir, out _);
        var args = new UnobservedTaskExceptionEventArgs(new AggregateException(Thrown()));

        reporter.OnUnobservedTask(null, args);

        Assert.True(args.Observed);
        string report = Assert.Single(Reports(dir));
        Assert.Contains("TaskScheduler.UnobservedTaskException", File.ReadAllText(report), StringComparison.Ordinal);
    }

    [Fact]
    public void CallbackDelHiloDeLaInterfaz_AsyncVoidQueLanza_DejaSuInformeYNoCierra()
    {
        using var dir = new TempDirectory();
        CrashReporter reporter = NewReporter(dir, out _);
        var context = new GuardedSynchronizationContext(new InlineContext(),
            ex => reporter.Write(CrashKind.DispatcherCallback, ex, fatal: false) is not null);
        SynchronizationContext? previous = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            ThrowingAsyncVoid();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        string report = Assert.Single(Reports(dir));
        string text = File.ReadAllText(report);
        Assert.Contains("DispatcherQueue", text, StringComparison.Ordinal);
        Assert.Contains("falló el manejador", text, StringComparison.Ordinal);
    }

    [Fact]
    public void CallbackDelHiloDeLaInterfaz_SiNoSeAtiende_SigueSuCamino()
    {
        var context = new GuardedSynchronizationContext(new SynchronizationContext(), _ => false);

        Assert.Throws<InvalidOperationException>(() => context.Send(_ => throw new InvalidOperationException("sin atender"), null));
    }

    [Fact]
    public unsafe void FalloNativo_Stowed_DejaInformeYMinivolcado()
    {
        using var dir = new TempDirectory();
        CrashReporter reporter = NewReporter(dir, out _);

        string? path = reporter.WriteNativeFatal(CrashReporter.StowedExceptionCode, null);

        Assert.NotNull(path);
        string text = File.ReadAllText(path);
        Assert.Contains("0xC000027B", text, StringComparison.Ordinal);
        Assert.Contains("stowed", text, StringComparison.Ordinal);
        string dump = Assert.Single(Directory.GetFiles(dir.Path, "*.dmp"));
        Assert.Contains("Minivolcado: " + CrashReportScrubber.Default.Scrub(dump), text, StringComparison.Ordinal);
        AssertMinidump(dump);
    }

    [Fact]
    public unsafe void FalloNativo_TrasUnInformeFatal_SoloSumaElMinivolcado()
    {
        using var dir = new TempDirectory();
        CrashReporter reporter = NewReporter(dir, out _);
        string? managed = reporter.Write(CrashKind.AppDomainUnhandled, Thrown(), fatal: true);

        string? native = reporter.WriteNativeFatal(0xC0000005, null);

        Assert.Equal(managed, native);
        Assert.Single(Reports(dir));
        Assert.Contains("Minivolcado:", File.ReadAllText(managed!), StringComparison.Ordinal);
    }

    [WindowsOnlyFact("MiniDumpWriteDump de dbghelp y un proceso hijo con cmd.exe.")]
    public async Task Minivolcado_EsUnArchivoMdmp()
    {
        // De otro proceso: volcar el de las pruebas desde dentro puede trabarse con los demás hilos.
        using var dir = new TempDirectory();
        string path = dir.File("prueba.dmp");
        // El hijo avisa por su salida cuando ya corre: volcar un proceso recién creado, con el cargador a medias, falla
        // (ERROR_PARTIAL_COPY) y hacía caer la prueba de vez en cuando a los pocos milisegundos.
        using var child = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c echo listo& ping -n 30 127.0.0.1 >nul")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
        })!;
        try
        {
            // Con plazo: si el hijo no llega a escribir, la prueba falla (TimeoutException) en vez de colgar la CI.
            string? ready = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal("listo", ready?.Trim());
            // Aun así, si el sistema está cargado, unos reintentos breves antes de darlo por fallido.
            bool written = false;
            int error = 0;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!(written = NativeCrashHandler.WriteMinidump(path, child, out error)) && clock.Elapsed < TimeSpan.FromSeconds(10))
            {
                await Task.Delay(200);
            }

            Assert.True(written, string.Create(CultureInfo.InvariantCulture,
                $"MiniDumpWriteDump falló durante {clock.Elapsed.TotalSeconds:0.0} s (último error: {error}, 0x{error:X8})."));
            AssertMinidump(path);
        }
        finally
        {
            child.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public void SesionQueTerminoSinCerrarse_SeVuelveInformeAlSiguienteArranque()
    {
        using var dir = new TempDirectory();
        CrashReporter crashed = NewReporter(dir, out SessionLog crashedLog);
        crashed.StartSession(processId: 4242, processStartTicks: 1234);
        crashedLog.Append("eligió escala");
        crashedLog.Dispose();

        CrashReporter next = NewReporter(dir, out _);
        IReadOnlyList<string> written = next.RecoverUncleanSessions((_, _) => false);

        string report = Assert.Single(written);
        string text = File.ReadAllText(report);
        Assert.Contains("terminó sin cerrarse", text, StringComparison.Ordinal);
        Assert.Contains("eligió escala", text, StringComparison.Ordinal);
        Assert.Equal(new[] { report }, next.PendingReports());
        Assert.Empty(next.RecoverUncleanSessions((_, _) => false));
    }

    [Fact]
    public void SesionConCierreLimpio_OConInformeFatal_NoDejaOtroInforme()
    {
        using var dir = new TempDirectory();
        CrashReporter clean = NewReporter(dir, out SessionLog cleanLog);
        clean.StartSession(processId: 1, processStartTicks: 1);
        cleanLog.Append("todo bien");
        clean.EndSession();

        CrashReporter fatal = NewReporter(dir, out _);
        fatal.StartSession(processId: 2, processStartTicks: 2);
        fatal.Write(CrashKind.AppDomainUnhandled, Thrown(), fatal: true);

        Assert.Empty(NewReporter(dir, out _).RecoverUncleanSessions((_, _) => false));
        Assert.Single(Reports(dir));
    }

    [Fact]
    public void SesionDeUnProcesoVivo_NoSeToca()
    {
        using var dir = new TempDirectory();
        CrashReporter other = NewReporter(dir, out SessionLog otherLog);
        other.StartSession(processId: 7, processStartTicks: 7);
        otherLog.Append("otra instancia abierta");

        Assert.Empty(NewReporter(dir, out _).RecoverUncleanSessions((pid, ticks) => pid == 7 && ticks == 7));
        otherLog.Dispose();
    }

    [Fact]
    public void ElInformeLlevaLasUltimas200LineasDelRegistro()
    {
        using var dir = new TempDirectory();
        CrashReporter reporter = NewReporter(dir, out SessionLog log);
        for (int i = 0; i < 250; i++)
        {
            log.Append(string.Create(CultureInfo.InvariantCulture, $"línea {i:000}"));
        }

        string text = File.ReadAllText(reporter.Write(CrashKind.Caught, "algo", fatal: false)!);

        Assert.Equal(SessionLog.DefaultCapacity, log.Tail().Count);
        Assert.Contains("Últimas 200 líneas", text, StringComparison.Ordinal);
        Assert.Contains("línea 050", text, StringComparison.Ordinal);
        Assert.Contains("línea 249", text, StringComparison.Ordinal);
        Assert.DoesNotContain("línea 049", text, StringComparison.Ordinal);
    }

    [Fact]
    public void LaMismaExcepcionPorDosVias_DejaUnSoloInforme()
    {
        using var dir = new TempDirectory();
        CrashReporter reporter = NewReporter(dir, out _);
        Exception ex = Thrown();

        string? first = reporter.Write(CrashKind.DispatcherCallback, ex, fatal: true);
        string? second = reporter.Write(CrashKind.XamlUnhandled, ex, fatal: true);

        Assert.Equal(first, second);
        Assert.Single(Reports(dir));
    }

    [Fact]
    public void AvisarDeLosPendientes_LosMarcaComoVistos()
    {
        using var dir = new TempDirectory();
        CrashReporter reporter = NewReporter(dir, out _);
        reporter.Write(CrashKind.AppDomainUnhandled, Thrown(), fatal: true);
        IReadOnlyList<string> pending = reporter.PendingReports();

        string message = CrashReporter.DescribePending(pending);
        reporter.AcknowledgePending();

        Assert.Contains(Path.Combine(CrashReporter.FolderName, Path.GetFileName(pending[0])), message, StringComparison.Ordinal);
        Assert.Empty(reporter.PendingReports());
    }

    [Fact]
    public void SeConservanLosUltimosInformes()
    {
        using var dir = new TempDirectory();
        DateTime now = new(2026, 10, 13, 10, 0, 0, DateTimeKind.Local);
        var reporter = new CrashReporter(dir.Path, Version, new SessionLog(), () => Gpu, () => now = now.AddSeconds(1));

        for (int i = 0; i < CrashReporter.KeepReports + 5; i++)
        {
            reporter.Write(CrashKind.Caught, "error " + (char)('a' + (i / 26)) + (char)('a' + (i % 26)), fatal: false); // Distintos: la firma no mira los números.
        }

        Assert.Equal(CrashReporter.KeepReports, Reports(dir).Length);
    }

    [Fact]
    public void ElRegistroDeTrace_LlegaAlRegistroDeLaSesion()
    {
        var log = new SessionLog();
        using var listener = new SessionLogTraceListener(log);

        listener.Write("Rodaje: ");
        listener.WriteLine("el módulo no respondió");

        Assert.EndsWith("Rodaje: el módulo no respondió", Assert.Single(log.Tail()), StringComparison.Ordinal);
    }

    private static CrashReporter NewReporter(TempDirectory dir, out SessionLog log)
    {
        log = new SessionLog();
        return new CrashReporter(dir.Path, Version, log, () => Gpu) { MinidumpWriter = FakeMinidump };
    }

    // El minivolcado de verdad se prueba con otro proceso (Minivolcado_EsUnArchivoMdmp).
    private static int FakeMinidump(string path, nint exceptionPointers)
    {
        File.WriteAllBytes(path, "MDMP"u8.ToArray());
        return 0;
    }

    private static string[] Reports(TempDirectory dir) => Directory.GetFiles(dir.Path, "*.txt");

    private static void AssertMinidump(string path)
    {
        byte[] head = new byte[4];
        using (FileStream stream = File.OpenRead(path))
        {
            stream.ReadExactly(head);
        }

        Assert.Equal("MDMP"u8.ToArray(), head);
    }

    private static InvalidOperationException Thrown()
    {
        try
        {
            ThrowingMethod();
            return new InvalidOperationException("no llegó");
        }
        catch (InvalidOperationException ex)
        {
            return ex;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowingMethod() => throw new InvalidOperationException("falló el manejador de prueba");

// async void a propósito: así llega la excepción al contexto del hilo, como en un manejador de XAML.
    private static async void ThrowingAsyncVoid()
    {
        await Task.Yield();
        throw new InvalidOperationException("falló el manejador async void");
    }

    /// <summary>Ejecuta cada callback en el momento, como el hilo de la interfaz cuando vuelve a su cola.</summary>
    private sealed class InlineContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => d(state);
    }
}
