using Rodaje.App.Services.Crashes;
using Rodaje.App.Tests.Support;

namespace Rodaje.App.Tests.Crashes;

/// <summary>
/// Revisión de #129: <see cref="CrashReporter.RecoverUncleanSessions"/> conserva la copia del registro si el informe no se
/// escribe, pero sin tope (con el disco aún sin permitirlo se acumulaba una por arranque fallido); y si el informe se
/// escribía pero borrar la copia fallaba, el siguiente arranque escribía otro informe igual.
/// </summary>
public class CrashUnreportedSessionTests
{
    private const string Version = "1.0.0+prueba";

    [Fact]
    public void SinPoderEscribirInformes_LasCopiasConservadasTienenTope_YQuedanLasMasNuevas()
    {
        using var dir = new TempDirectory();
        int total = CrashReporter.KeepUnreportedSessions + 4;
        DateTime start = DateTime.UtcNow.AddHours(-2);
        var sessions = new List<string>();
        for (int i = 0; i < total; i++)
        {
            string session = CrashedSession(dir, 5000 + i, $"sesión {i}");
            File.SetLastWriteTimeUtc(session, start.AddMinutes(i));
            sessions.Add(session);
        }

        var log = new SessionLog();
        var failing = new CrashReporter(dir.Path, Version, log) { ReportWriter = (_, _) => false };
        Assert.Empty(failing.RecoverUncleanSessions((_, _) => false));

        Assert.Equal(sessions.Skip(total - CrashReporter.KeepUnreportedSessions).Order(StringComparer.Ordinal),
            Directory.GetFiles(Path.GetDirectoryName(sessions[0])!, "*.log").Order(StringComparer.Ordinal));
        Assert.Equal(4, log.Tail().Count(l => l.Contains("Se descartó el registro de la sesión anterior", StringComparison.Ordinal)));
    }

    [Fact]
    public void ElInformeSeEscribeYBorrarLaCopiaFalla_ElSiguienteArranqueNoLoDuplica()
    {
        using var dir = new TempDirectory();
        string session = CrashedSession(dir, 4242, "eligió escala");

        var log = new SessionLog();
        var first = new CrashReporter(dir.Path, Version, log) { SessionDeleter = _ => throw new IOException("en uso") };
        string report = Assert.Single(first.RecoverUncleanSessions((_, _) => false));
        Assert.True(File.Exists(session));
        Assert.Contains(log.Tail(), l => l.Contains("ya informada", StringComparison.Ordinal));

        // El siguiente arranque borra la copia sin escribir otro informe.
        var next = new CrashReporter(dir.Path, Version, new SessionLog());
        Assert.Empty(next.RecoverUncleanSessions((_, _) => false));

        Assert.False(File.Exists(session));
        Assert.Equal([report], Directory.GetFiles(dir.Path, "*.txt"));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(session)!));
    }

    [Fact]
    public void LaCopiaSigueSinPoderBorrarse_TampocoDuplicaEnArranquesSiguientes()
    {
        using var dir = new TempDirectory();
        string session = CrashedSession(dir, 4242, "x");
        for (int i = 0; i < 3; i++)
        {
            var reporter = new CrashReporter(dir.Path, Version, new SessionLog()) { SessionDeleter = _ => throw new UnauthorizedAccessException("sin permiso") };
            IReadOnlyList<string> written = reporter.RecoverUncleanSessions((_, _) => false);
            Assert.Equal(i == 0 ? 1 : 0, written.Count);
        }

        Assert.True(File.Exists(session));
        Assert.Single(Directory.GetFiles(dir.Path, "*.txt"));
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
