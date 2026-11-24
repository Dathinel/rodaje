using System.Diagnostics;
using System.Globalization;

namespace Rodaje.Media.Proxies;

/// <summary>
/// Que los procesos hijos (FFmpeg de proxies, exportación, sonoridad y verificación) no sobrevivan a Rodaje. En Windows,
/// <see cref="ChildProcessJob"/>: el sistema los mata al cerrarse el trabajo. En Linux y macOS no hay nada equivalente
/// para un proceso ajeno: cada hijo se apunta (PID y hora de inicio) en un registro por sesión del editor; al salir
/// normalmente se matan los que sigan vivos y, si el editor murió de golpe, el siguiente arranque los barre con
/// <see cref="SweepOrphans()"/>. La hora de inicio evita matar a un proceso que heredó el PID.
/// </summary>
public static class ChildProcessGuard
{
    private static readonly Lock Gate = new();
    private static string? _sessionFile;

    /// <summary>Pone el proceso bajo la guarda; si no se puede, el proceso sigue igual sin esa garantía.</summary>
    public static bool TryAssign(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (OperatingSystem.IsWindowsVersionAtLeast(7))
        {
            return ChildProcessJob.TryAssign(process);
        }

        try
        {
            lock (Gate)
            {
                if (_sessionFile is null)
                {
                    using Process self = Process.GetCurrentProcess();
                    _sessionFile = SessionFile(RegistryDirectory(), self.Id, self.StartTime);
                    AppDomain.CurrentDomain.ProcessExit += (_, _) => KillRegistered(_sessionFile);
                }

                Register(_sessionFile, process.Id, process.StartTime);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Mata a los hijos que dejaron sesiones del editor que ya no viven y borra sus registros. Devuelve cuántos mató.
    /// En Windows no hace nada: ahí el sistema se encarga.
    /// </summary>
    public static int SweepOrphans() => OperatingSystem.IsWindows() ? 0 : SweepOrphans(RegistryDirectory());

    /// <summary>Carpeta del registro: <c>$XDG_RUNTIME_DIR/rodaje/hijos</c> o, sin ella, la temporal del usuario.</summary>
    internal static string RegistryDirectory()
    {
        string? runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        string root = !string.IsNullOrEmpty(runtime) && Directory.Exists(runtime)
            ? Path.Combine(runtime, "rodaje")
            : Path.Combine(Path.GetTempPath(), "rodaje-" + Environment.UserName);
        return Path.Combine(root, "hijos");
    }

    internal static string SessionFile(string directory, int pid, DateTime start)
    {
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, string.Create(CultureInfo.InvariantCulture, $"{pid}-{start.ToUniversalTime().Ticks}.txt"));
    }

    internal static void Register(string sessionFile, int pid, DateTime start) =>
        File.AppendAllText(sessionFile, string.Create(CultureInfo.InvariantCulture, $"{pid} {start.ToUniversalTime().Ticks}\n"));

    internal static int SweepOrphans(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        int killed = 0;
        foreach (string file in Directory.EnumerateFiles(directory, "*.txt"))
        {
            string[] owner = Path.GetFileNameWithoutExtension(file).Split('-');
            if (owner.Length == 2 && int.TryParse(owner[0], CultureInfo.InvariantCulture, out int pid)
                && long.TryParse(owner[1], CultureInfo.InvariantCulture, out long ticks) && IsAlive(pid, ticks))
            {
                continue;
            }

            killed += KillRegistered(file);
        }

        return killed;
    }

    private static int KillRegistered(string file)
    {
        int killed = 0;
        string[] lines;
        try
        {
            lines = File.ReadAllLines(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }

        foreach (string line in lines)
        {
            string[] parts = line.Split(' ');
            if (parts.Length == 2 && int.TryParse(parts[0], CultureInfo.InvariantCulture, out int pid)
                && long.TryParse(parts[1], CultureInfo.InvariantCulture, out long ticks) && TryGet(pid, ticks) is { } child)
            {
                using (child)
                {
                    try
                    {
                        child.Kill(entireProcessTree: true);
                        killed++;
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                    {
                        // Terminó solo entre la lectura y el intento.
                    }
                }
            }
        }

        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Lo barre el siguiente arranque.
        }

        return killed;
    }

    private static bool IsAlive(int pid, long startTicks)
    {
        using Process? process = TryGet(pid, startTicks);
        return process is not null;
    }

    // El mismo PID con otra hora de inicio es otro proceso. La hora sale de /proc o del núcleo; se tolera un segundo.
    private static Process? TryGet(int pid, long startTicks)
    {
        Process? process = null;
        try
        {
            process = Process.GetProcessById(pid);
            if (!process.HasExited && Math.Abs(process.StartTime.ToUniversalTime().Ticks - startTicks) <= TimeSpan.TicksPerSecond)
            {
                return process;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // No existe o no se puede leer: no es nuestro.
        }

        process?.Dispose();
        return null;
    }
}
