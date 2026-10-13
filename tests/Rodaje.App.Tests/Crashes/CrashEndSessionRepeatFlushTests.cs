using Rodaje.App.Services.Crashes;
using Rodaje.App.Tests.Support;

namespace Rodaje.App.Tests.Crashes;

/// <summary>
/// Tarea 292: <c>Program.Main</c> llama a <see cref="CrashReporter.EndSession"/> al volver de <c>Application.Start</c> y
/// otra vez antes de devolver el código. Un fallo que se repite entre las dos llamadas tiene que dejar su línea de
/// repeticiones en el informe aunque la copia del registro ya se haya cerrado con la primera.
/// </summary>
public class CrashEndSessionRepeatFlushTests
{
    private static readonly DateTime Start = new(2026, 10, 13, 10, 0, 0, DateTimeKind.Local);

    [Fact]
    public void EndSession_LaSegundaVez_AnotaLasRepeticionesDeDespuesDeLaPrimera()
    {
        using var dir = new TempDirectory();
        DateTime now = Start;
        var log = new SessionLog();
        var reporter = new CrashReporter(dir.Path, "1.0.0+prueba", log, clock: () => now);
        reporter.StartSession(processId: 21, processStartTicks: 21);
        string mirror = log.MirrorPath!;
        reporter.EndSession();
        Assert.False(File.Exists(mirror));

        // El mismo fallo dos veces tras el primer cierre: el primero deja informe y el segundo cuenta como repetición.
        string? report = reporter.Write(CrashKind.Caught, "System.InvalidOperationException: algo falló", fatal: false, out bool first);
        now = now.AddSeconds(1);
        reporter.Write(CrashKind.Caught, "System.InvalidOperationException: algo falló", fatal: false, out bool repeated);
        Assert.NotNull(report);
        Assert.False(first);
        Assert.True(repeated);
        Assert.DoesNotContain("Repetido", File.ReadAllText(report), StringComparison.Ordinal);

        reporter.EndSession();

        Assert.Contains("Repetido 1 veces más hasta las 10:00:01.000", File.ReadAllText(report), StringComparison.Ordinal);
        Assert.False(File.Exists(mirror));
    }

    [Fact]
    public async Task EndSession_LaSegundaVez_ConElCerrojoTomado_NoEsperaNiAnota()
    {
        using var dir = new TempDirectory();
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        DateTime now = Start;
        var log = new SessionLog();
        bool block = false;
        var reporter = new CrashReporter(dir.Path, "1.0.0+prueba", log, clock: () => now)
        {
            // Un plazo largo: si la segunda llamada esperara el cerrojo, la prueba se quedaría aquí.
            GateTimeout = TimeSpan.FromMinutes(5),
            ReportWriter = (path, text) =>
            {
                if (block)
                {
                    entered.Set();
                    _ = release.Wait(TimeSpan.FromSeconds(60));
                    return true;
                }

                // Los informes de antes de bloquear sí quedan en disco: ahí se anotan las repeticiones.
                File.WriteAllText(path, text);
                return true;
            },
        };
        reporter.StartSession(processId: 22, processStartTicks: 22);
        reporter.EndSession();

        // Un fallo repetido tras el primer cierre: su repetición queda pendiente de anotar.
        string? report = reporter.Write(CrashKind.Caught, "System.InvalidOperationException: algo falló", fatal: false, out bool first);
        now = now.AddSeconds(1);
        reporter.Write(CrashKind.Caught, "System.InvalidOperationException: algo falló", fatal: false, out bool repeated);
        Assert.NotNull(report);
        Assert.False(first);
        Assert.True(repeated);
        Assert.DoesNotContain("Repetido", File.ReadAllText(report), StringComparison.Ordinal);

        // Otro fallo, distinto, se queda a mitad de su informe con el cerrojo tomado en otro hilo.
        block = true;
        Task stuck = Task.Run(() => reporter.Write(CrashKind.Caught, "System.ArgumentException: otro fallo distinto", fatal: false));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(30)));
        try
        {
            Task second = Task.Run(reporter.EndSession);
            Assert.Same(second, await Task.WhenAny(second, Task.Delay(TimeSpan.FromSeconds(30))));
            Assert.DoesNotContain(log.Tail(), l => l.Contains("Cierre de la sesión sin esperar", StringComparison.Ordinal));

            // Sin el cerrojo no se toca el informe: la repetición sigue pendiente.
            Assert.DoesNotContain("Repetido", File.ReadAllText(report), StringComparison.Ordinal);
        }
        finally
        {
            release.Set();
            await stuck.WaitAsync(TimeSpan.FromSeconds(30));
        }

        // Con el cerrojo libre, la siguiente llamada sí la anota.
        reporter.EndSession();

        Assert.Contains("Repetido 1 veces más hasta las 10:00:01.000", File.ReadAllText(report), StringComparison.Ordinal);
    }
}
