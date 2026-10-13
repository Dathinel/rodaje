using Rodaje.App.Services.Crashes;
using Rodaje.App.Tests.Support;

namespace Rodaje.App.Tests.Crashes;

/// <summary>
/// Tarea 242, punto 2: <c>Program.Main</c> llama a <see cref="CrashReporter.EndSession"/> dos veces (al volver de
/// <c>Application.Start</c> y antes de devolver el código), y también lo llaman <c>WM_ENDSESSION</c> y la salida del
/// proceso. Solo la primera cierra la sesión; las demás no esperan el cerrojo, no anotan nada y no lanzan.
/// </summary>
public class CrashEndSessionTwiceTests
{
    private const string GaveUpWaiting = "Cierre de la sesión sin esperar";

    [Fact]
    public async Task EndSession_LaSegundaVez_NoEsperaElCerrojoNiAnotaNada()
    {
        using var dir = new TempDirectory();
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var log = new SessionLog();
        bool block = false;
        var reporter = new CrashReporter(dir.Path, "1.0.0+prueba", log)
        {
            // Plazo cero: sin el arreglo, la segunda llamada no consigue el cerrojo y anota que cerró sin esperar.
            GateTimeout = TimeSpan.Zero,
            ReportWriter = (_, _) =>
            {
                if (block)
                {
                    entered.Set();
                    _ = release.Wait(TimeSpan.FromSeconds(60));
                }

                return true;
            },
        };
        reporter.StartSession(processId: 12, processStartTicks: 12);
        string mirror = log.MirrorPath!;

        reporter.EndSession();
        Assert.False(File.Exists(mirror));
        Assert.DoesNotContain(log.Tail(), l => l.Contains(GaveUpWaiting, StringComparison.Ordinal));

        // Otro hilo queda a mitad de un informe, con el cerrojo tomado.
        block = true;
        Task stuck = Task.Run(() => reporter.Write(CrashKind.Caught, "algo", fatal: false));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(30)));
        try
        {
            reporter.EndSession();

            Assert.DoesNotContain(log.Tail(), l => l.Contains(GaveUpWaiting, StringComparison.Ordinal));
        }
        finally
        {
            release.Set();
            await stuck.WaitAsync(TimeSpan.FromSeconds(30));
        }
    }

    [Fact]
    public void EndSession_TrasUnaSesionNueva_VuelveACerrarla()
    {
        using var dir = new TempDirectory();
        var log = new SessionLog();
        var reporter = new CrashReporter(dir.Path, "1.0.0+prueba", log);

        reporter.StartSession(processId: 13, processStartTicks: 13);
        reporter.EndSession();
        reporter.EndSession();
        reporter.StartSession(processId: 13, processStartTicks: 14);
        string mirror = log.MirrorPath!;
        reporter.EndSession();

        Assert.False(File.Exists(mirror));
    }
}
