using Rodaje.App.Services.Crashes;
using Rodaje.App.Tests.Support;

namespace Rodaje.App.Tests.Crashes;

/// <summary>
/// Revisión de #149: el tope de <see cref="CrashReporter.KeepUnreportedSessions"/> copias contaba también las ya marcadas
/// como informadas (su informe se escribió pero no se pudieron borrar), y diez de esas más nuevas desplazaban a una copia
/// sin informe, que es la única evidencia de su cierre.
/// </summary>
public class CrashReportedSessionPruneTests
{
    private const string Version = "1.0.0+prueba";

    [Fact]
    public void LasMarcadasComoInformadasSeDescartanAntesQueUnaSinInforme()
    {
        using var dir = new TempDirectory();
        DateTime start = DateTime.UtcNow.AddHours(-2);

        // La sin informe es la más vieja.
        string unreported = CrashedSession(dir, 6000, "sin informe");
        File.SetLastWriteTimeUtc(unreported, start);

        var marked = new List<string>();
        for (int i = 0; i < CrashReporter.KeepUnreportedSessions; i++)
        {
            string session = CrashedSession(dir, 6100 + i, $"informada {i}");
            File.WriteAllText(Path.ChangeExtension(session, ".informada"), "");
            File.SetLastWriteTimeUtc(session, start.AddMinutes(10 + i));
            marked.Add(session);
        }

        var log = new SessionLog();
        var reporter = new CrashReporter(dir.Path, Version, log)
        {
            ReportWriter = (_, _) => false,
            SessionDeleter = _ => throw new IOException("en uso"),
        };
        Assert.Empty(reporter.RecoverUncleanSessions((_, _) => false));

        string folder = Path.GetDirectoryName(unreported)!;
        Assert.True(File.Exists(unreported));
        Assert.Equal(CrashReporter.KeepUnreportedSessions, Directory.GetFiles(folder, "*.log").Length);

        // Se descartó la marcada más vieja, con su marca.
        Assert.False(File.Exists(marked[0]));
        Assert.False(File.Exists(Path.ChangeExtension(marked[0], ".informada")));
        Assert.All(marked.Skip(1), f => Assert.True(File.Exists(f)));
    }

    // Una sesión de un proceso que terminó sin cerrarse: su copia del registro queda en sesiones\.
    private static string CrashedSession(TempDirectory dir, int processId, string line)
    {
        var crashedLog = new SessionLog();
        var crashed = new CrashReporter(dir.Path, Version, crashedLog);
        crashed.StartSession(processId, processStartTicks: 1234);
        crashedLog.Append(line);
        string session = crashedLog.MirrorPath!;
        crashedLog.Dispose();
        return session;
    }
}
