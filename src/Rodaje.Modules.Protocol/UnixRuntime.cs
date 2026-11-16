using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Rodaje.Modules.Protocol;

/// <summary>
/// Lo que el protocolo necesita de Linux y macOS (D-066): la carpeta 0700 de la sesión del editor con los sockets y las
/// regiones, las credenciales del otro extremo del socket y que el módulo muera si muere el editor. Probado antes en
/// <c>experimentos/linux-fase0/eco</c>.
/// </summary>
/// <remarks>
/// Rutas: <c>$XDG_RUNTIME_DIR/rodaje/e.&lt;pid&gt;</c> en Linux (tmpfs del usuario) o <c>/tmp/rodaje-&lt;uid&gt;/e.&lt;pid&gt;</c>;
/// en macOS siempre la segunda, porque <c>$TMPDIR</c> más el nombre del socket se pasa de los 104 bytes de
/// <c>sun_path</c>. Cada sesión barre al empezar las carpetas de editores que ya no viven.
/// </remarks>
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
internal static partial class UnixRuntime
{
    /// <summary>Prefijo de la carpeta de cada sesión del editor dentro de la base.</summary>
    internal const string SessionPrefix = "e.";

    /// <summary>Prefijo del socket de cada módulo: <c>m.</c> y 16 hexadecimales.</summary>
    internal const string SocketPrefix = "m.";

    // sun_path: 108 bytes en Linux, 104 en macOS (sys/un.h), con el nulo final.
    internal static int SocketPathLimit => OperatingSystem.IsMacOS() ? 103 : 107;

    private const UnixFileMode Mode0700 = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private static readonly Lazy<string> Session = new(CreateSessionDirectory);

    /// <summary>La carpeta de esta sesión, creada 0700 la primera vez (y barridas las de sesiones muertas).</summary>
    internal static string SessionDirectory => Session.Value;

    internal static string BaseDirectory()
    {
        if (OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } xdg && Path.IsPathRooted(xdg))
        {
            return Path.Combine(xdg, "rodaje");
        }

        return "/tmp/rodaje-" + GetEuid().ToString(CultureInfo.InvariantCulture);
    }

    internal static string NewSocketPath() =>
        Path.Combine(SessionDirectory, SocketPrefix + Guid.NewGuid().ToString("N")[..16]);

    /// <summary>
    /// ¿Es <paramref name="path"/> un socket que pudo crear el editor? Absoluto, sin <c>..</c>, que quepa en
    /// <c>sun_path</c> y con nombre <c>m.&lt;16 hex&gt;</c>.
    /// </summary>
    internal static bool IsValidSocketPath(string? path)
    {
        if (path is null || !Path.IsPathRooted(path) || System.Text.Encoding.UTF8.GetByteCount(path) > SocketPathLimit
            || path.Contains("/../", StringComparison.Ordinal) || path.Contains("/./", StringComparison.Ordinal))
        {
            return false;
        }

        string name = Path.GetFileName(path);
        return name.Length == SocketPrefix.Length + 16 && name.StartsWith(SocketPrefix, StringComparison.Ordinal)
            && name[SocketPrefix.Length..].All(char.IsAsciiHexDigitLower);
    }

    /// <summary>Crea <paramref name="path"/> con 0700 o comprueba que la que hay es nuestra y 0700.</summary>
    /// <exception cref="ModuleProtocolException">La carpeta es de otro usuario, un enlace, o no se pudo dejar en 0700.</exception>
    internal static void EnsurePrivateDirectory(string path)
    {
        try
        {
            Directory.CreateDirectory(path, Mode0700);
            var info = new DirectoryInfo(path);
            if (info.LinkTarget is not null)
            {
                throw new ModuleProtocolException($"{path} es un enlace; los módulos necesitan una carpeta propia.");
            }

            // chmod solo lo puede hacer el dueño: si la carpeta es de otro usuario, falla aquí (EPERM).
            File.SetUnixFileMode(path, Mode0700);
            if (File.GetUnixFileMode(path) != Mode0700)
            {
                throw new ModuleProtocolException($"{path} no quedó con permisos 0700.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ModuleProtocolException($"No se pudo preparar la carpeta de los módulos {path}: {ex.Message}", ex);
        }
    }

    /// <summary>Borra las carpetas de sesión de editores que ya no viven. Devuelve cuántas quitó.</summary>
    internal static int SweepOrphans(string baseDirectory)
    {
        if (!Directory.Exists(baseDirectory))
        {
            return 0;
        }

        int removed = 0;
        foreach (string directory in Directory.EnumerateDirectories(baseDirectory, SessionPrefix + "*"))
        {
            if (!int.TryParse(Path.GetFileName(directory)[SessionPrefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out int pid)
                || pid == Environment.ProcessId || IsAlive(pid))
            {
                continue;
            }

            try
            {
                Directory.Delete(directory, recursive: true);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Otro editor lo está barriendo a la vez.
            }
        }

        return removed;
    }

    /// <summary>PID del proceso al otro lado de un socket de dominio Unix aceptado.</summary>
    /// <exception cref="ModuleProtocolException">El sistema no lo dio.</exception>
    internal static unsafe int PeerProcessId(SafeHandle socket)
    {
        int fd = (int)socket.DangerousGetHandle();
        if (OperatingSystem.IsLinux())
        {
            // SO_PEERCRED (asm-generic/socket.h: SOL_SOCKET 1, SO_PEERCRED 17) da struct ucred { pid, uid, gid }.
            int* credentials = stackalloc int[3];
            uint length = 12;
            if (GetSockOpt(fd, 1, 17, credentials, &length) != 0 || length < 12)
            {
                throw new ModuleProtocolException($"No se pudo leer SO_PEERCRED (errno {Marshal.GetLastPInvokeError()}).");
            }

            return credentials[0];
        }

        // LOCAL_PEERPID (XNU sys/un.h: SOL_LOCAL 0, LOCAL_PEERPID 2) da pid_t.
        int pid;
        uint pidLength = 4;
        if (GetSockOpt(fd, 0, 2, &pid, &pidLength) != 0 || pidLength < 4)
        {
            throw new ModuleProtocolException($"No se pudo leer LOCAL_PEERPID (errno {Marshal.GetLastPInvokeError()}).");
        }

        return pid;
    }

    /// <summary>
    /// Mata el módulo si su padre (el editor) muere: un hilo mira <c>getppid</c> cada 250 ms y, si cambia (el núcleo lo
    /// pasó a otro padre), se manda SIGKILL. No usa <c>PR_SET_PDEATHSIG</c>: ese aviso sigue al hilo que creó el proceso,
    /// y en .NET ese hilo puede ser uno del pool que se retira solo, lo que mataría módulos sanos.
    /// </summary>
    internal static void ExitWhenParentDies()
    {
        int parent = GetPPid();
        var watcher = new Thread(() =>
        {
            while (GetPPid() == parent)
            {
                Thread.Sleep(250);
            }

            _ = Kill(Environment.ProcessId, 9);
        })
        { IsBackground = true, Name = "Rodaje: vigila al editor" };
        watcher.Start();
    }

    private static string CreateSessionDirectory()
    {
        string root = BaseDirectory();
        EnsurePrivateDirectory(root);
        _ = SweepOrphans(root);
        string session = Path.Combine(root, SessionPrefix + Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        EnsurePrivateDirectory(session);
        return session;
    }

    // kill(pid, 0): existe si da 0 o EPERM (ESRCH = 3, igual en los dos sistemas).
    private static bool IsAlive(int pid) => Kill(pid, 0) == 0 || Marshal.GetLastPInvokeError() != 3;

    [LibraryImport("libc", EntryPoint = "getsockopt", SetLastError = true)]
    private static unsafe partial int GetSockOpt(int fd, int level, int name, void* value, uint* length);

    [LibraryImport("libc", EntryPoint = "geteuid")]
    internal static partial uint GetEuid();

    [LibraryImport("libc", EntryPoint = "getppid")]
    private static partial int GetPPid();

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int Kill(int pid, int signal);
}
