using Rodaje.App.Services.Crashes;
using Rodaje.App.Tests.Support;

namespace Rodaje.App.Tests.Crashes;

/// <summary>
/// Tarea 107: las corridas automáticas no dejan falsos «Rodaje se cerró por un error», el cierre de Windows cierra la
/// sesión y el filtro nativo escribe el texto antes del minivolcado, con un vigilante que corta si el volcado se traba.
/// </summary>
public class CrashSessionTests
{
    [Fact]
    public void CorridaAutomatica_NoAbreLaCopiaDelRegistro_NiRecuperaSesiones()
    {
        using var dir = new TempDirectory();
        var crashedLog = new SessionLog();
        var crashed = new CrashReporter(dir.Path, "1.0.0+prueba", crashedLog);
        crashed.StartSession(processId: 4242, processStartTicks: 1234);
        crashedLog.Append("sesión del usuario que terminó mal");
        crashedLog.Dispose();
        string sessions = Path.Combine(dir.Path, "sesiones");

        var log = new SessionLog();
        var automated = new CrashReporter(dir.Path, "1.0.0+prueba", log);
        IReadOnlyList<string> recovered = automated.BeginSession(automated: true);

        Assert.Empty(recovered);
        Assert.Null(log.MirrorPath);
        // La sesión del usuario sigue ahí para el próximo arranque normal, y no hay informe.
        Assert.Single(Directory.GetFiles(sessions, "*.log"));
        Assert.Empty(Directory.GetFiles(dir.Path, "*.txt"));

        // Matar la corrida automática (sin EndSession) no deja copia que el siguiente arranque tome por un fallo.
        log.Append("la herramienta mata la app");
        Assert.Single(Directory.GetFiles(sessions, "*.log"));
    }

    [Fact]
    public void CorridaNormal_RecuperaLaSesionAnterior_YAbreLaSuya()
    {
        using var dir = new TempDirectory();
        var crashedLog = new SessionLog();
        var crashed = new CrashReporter(dir.Path, "1.0.0+prueba", crashedLog);
        crashed.StartSession(processId: int.MaxValue - 7, processStartTicks: 1234);
        crashedLog.Append("eligió escala");
        crashedLog.Dispose();

        using var log = new SessionLog();
        var reporter = new CrashReporter(dir.Path, "1.0.0+prueba", log);
        IReadOnlyList<string> recovered = reporter.BeginSession(automated: false);

        Assert.Contains("eligió escala", File.ReadAllText(Assert.Single(recovered)), StringComparison.Ordinal);
        Assert.NotNull(log.MirrorPath);
        Assert.True(File.Exists(log.MirrorPath));
        string mirror = log.MirrorPath!;
        reporter.EndSession();
        Assert.False(File.Exists(mirror));
    }

    [Fact]
    public void WmEndSession_CierraLaSesion()
    {
        int ended = 0;

        Assert.True(SessionEndWatcher.Handle(SessionEndWatcher.EndSessionMessage, 1, () => ended++));
        // wParam 0: otra aplicación canceló el apagado y la sesión sigue.
        Assert.False(SessionEndWatcher.Handle(SessionEndWatcher.EndSessionMessage, 0, () => ended++));
        Assert.False(SessionEndWatcher.Handle(0x0010, 1, () => ended++));
        Assert.True(SessionEndWatcher.Handle(SessionEndWatcher.EndSessionMessage, 1, () => throw new IOException("disco")));

        Assert.Equal(1, ended);
    }

    [Fact]
    public void WmEndSession_ConElRegistroReal_BorraLaCopia()
    {
        using var dir = new TempDirectory();
        using var log = new SessionLog();
        var reporter = new CrashReporter(dir.Path, "1.0.0+prueba", log);
        reporter.StartSession(processId: 9, processStartTicks: 9);
        string mirror = log.MirrorPath!;

        SessionEndWatcher.Handle(SessionEndWatcher.EndSessionMessage, 1, reporter.EndSession);

        Assert.False(File.Exists(mirror));
        Assert.Empty(new CrashReporter(dir.Path, "1.0.0+prueba", new SessionLog()).RecoverUncleanSessions((_, _) => false));
    }

    [Fact]
    public unsafe void FalloNativo_ElTextoSeEscribeAntesDelMinivolcado()
    {
        using var dir = new TempDirectory();
        string? textWhenDumping = null;
        var reporter = new CrashReporter(dir.Path, "1.0.0+prueba", new SessionLog())
        {
            MinidumpWriter = (path, _) =>
            {
                textWhenDumping = File.ReadAllText(Assert.Single(Directory.GetFiles(dir.Path, "*.txt")));
                File.WriteAllBytes(path, "MDMP"u8.ToArray());
                return true;
            },
        };

        string? report = reporter.WriteNativeFatal(0xC0000005, null);

        Assert.NotNull(textWhenDumping);
        Assert.Contains("0xC0000005", textWhenDumping, StringComparison.Ordinal);
        string dump = Assert.Single(Directory.GetFiles(dir.Path, "*.dmp"));
        Assert.Equal(Path.ChangeExtension(report, ".dmp"), dump);
        Assert.Contains("Minivolcado: " + ExpectedDumpPath.Of(dump) + Environment.NewLine, File.ReadAllText(report!), StringComparison.Ordinal);
    }

    [Fact]
    public unsafe void FalloNativo_SiElMinivolcadoFalla_QuedaElTextoSinMencionarlo()
    {
        using var dir = new TempDirectory();
        var reporter = new CrashReporter(dir.Path, "1.0.0+prueba", new SessionLog()) { MinidumpWriter = (_, _) => false };

        string? report = reporter.WriteNativeFatal(CrashReporter.StowedExceptionCode, null);

        Assert.NotNull(report);
        Assert.DoesNotContain("Minivolcado", File.ReadAllText(report), StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(dir.Path, "*.dmp"));
    }

    [Fact]
    public unsafe void FalloNativo_SiElMinivolcadoSeTraba_ElVigilanteTerminaElProceso_YElTextoQueda()
    {
        using var dir = new TempDirectory();
        using var stuck = new ManualResetEventSlim(false);
        uint? terminatedWith = null;
        using var watchdog = new CrashWatchdog(TimeSpan.FromMilliseconds(200), code =>
        {
            terminatedWith = code;
            stuck.Set(); // En la app, TerminateProcess; aquí suelta el volcado trabado.
        });
        var reporter = new CrashReporter(dir.Path, "1.0.0+prueba", new SessionLog())
        {
            MinidumpWriter = (_, _) => stuck.Wait(TimeSpan.FromSeconds(30)) && false,
        };

        string? report = NativeCrashHandler.WriteGuarded(reporter, watchdog, 0xC0000005, null);

        Assert.Equal(0xC0000005u, terminatedWith);
        Assert.NotNull(report);
        Assert.Contains("0xC0000005", File.ReadAllText(report), StringComparison.Ordinal);
    }

    [Fact]
    public unsafe void FalloNativo_ATiempo_ElVigilanteNoHaceNada()
    {
        using var dir = new TempDirectory();
        int terminated = 0;
        using var watchdog = new CrashWatchdog(TimeSpan.FromMilliseconds(300), _ => Interlocked.Increment(ref terminated));
        var reporter = new CrashReporter(dir.Path, "1.0.0+prueba", new SessionLog())
        {
            MinidumpWriter = (path, _) =>
            {
                File.WriteAllBytes(path, "MDMP"u8.ToArray());
                return true;
            },
        };

        Assert.NotNull(NativeCrashHandler.WriteGuarded(reporter, watchdog, 0xC0000005, null));
        Thread.Sleep(600);

        Assert.Equal(0, Volatile.Read(ref terminated));
    }

    [Fact]
    public void Vigilante_SinDesarmar_TerminaConElCodigo()
    {
        using var fired = new ManualResetEventSlim(false);
        uint code = 0;
        using var watchdog = new CrashWatchdog(TimeSpan.FromMilliseconds(100), c =>
        {
            code = c;
            fired.Set();
        });

        watchdog.Arm(0xC000027B);

        Assert.True(fired.Wait(TimeSpan.FromSeconds(10)));
        Assert.Equal(0xC000027Bu, code);
    }
}
