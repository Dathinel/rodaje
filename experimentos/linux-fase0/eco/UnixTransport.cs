using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Rodaje.Modules.Protocol;

namespace Rodaje.Experimentos.Eco;

/// <summary>
/// Transporte Unix del protocolo de módulos: socket de dominio Unix en una ruta absoluta corta, dentro de una
/// carpeta propia con permisos 0700, y autenticación del otro extremo por credenciales del socket. Las partes
/// que solo son .NET no llevan marca de plataforma y sirven también en Windows (--solo-socket).
/// </summary>
internal static class UnixTransport
{
    public const int SunPathLinux = 108; // Linux struct sockaddr_un.sun_path (sys/un.h)
    public const int SunPathMac = 104;   // XNU struct sockaddr_un.sun_path (sys/un.h)
    public static int SunPathLimit => OperatingSystem.IsMacOS() ? SunPathMac : SunPathLinux;

    /// <summary>Prefijo de los directorios de ejecución: <c>e.&lt;pid&gt;</c> dentro de la carpeta base.</summary>
    public const string RunPrefix = "e.";

    public static Socket Listen(string path)
    {
        Socket socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.Bind(new UnixDomainSocketEndPoint(path));
        socket.Listen(8);
        return socket;
    }

    public static async Task<Socket> ConnectAsync(string path, CancellationToken cancellationToken = default)
    {
        Socket socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), cancellationToken).ConfigureAwait(false);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// La carpeta base 0700: en Linux <c>$XDG_RUNTIME_DIR/rodaje</c> (tmpfs) con respaldo <c>/tmp/rodaje-&lt;uid&gt;</c>;
    /// en macOS siempre <c>/tmp/rodaje-&lt;uid&gt;</c>, que es corta ($TMPDIR se pasa de sun_path).
    /// </summary>
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    public static string BaseDir()
    {
        if (OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } xdg)
            return Path.Combine(xdg, "rodaje");
        return $"/tmp/rodaje-{Posix.GetEuid()}";
    }

    /// <summary>La carpeta de esta ejecución: <c>e.&lt;pid&gt;</c> dentro de <paramref name="baseDir"/>.</summary>
    public static string NewRunDir(string baseDir) => Path.Combine(baseDir, RunPrefix + Environment.ProcessId);

    public static string NewSocketPath(string runDir) => Path.Combine(runDir, "m." + Guid.NewGuid().ToString("N")[..8]);

    /// <summary>Crea <paramref name="path"/> con 0700 (o aprieta sus permisos si ya existe) y devuelve si quedó 0700.</summary>
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    public static bool EnsureDir0700(string path)
    {
        const UnixFileMode modo700 = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        try
        {
            Directory.CreateDirectory(path, modo700);
            if (File.GetUnixFileMode(path) != modo700)
                File.SetUnixFileMode(path, modo700);
            return File.GetUnixFileMode(path) == modo700;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Borra los <c>e.&lt;pid&gt;</c> de <paramref name="baseDir"/> cuyo pid ya no existe. Devuelve cuántos quitó.</summary>
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    public static int SweepOrphans(string baseDir)
    {
        int removed = 0;
        foreach (string dir in Directory.EnumerateDirectories(baseDir, RunPrefix + "*"))
        {
            string rest = Path.GetFileName(dir)[RunPrefix.Length..];
            if (!int.TryParse(rest, out int pid) || pid == Environment.ProcessId) continue;
            if (PidAlive(pid)) continue;
            try
            {
                Directory.Delete(dir, recursive: true);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Otro proceso lo está usando o ya lo quitó.
            }
        }
        return removed;
    }

    /// <summary>Un pid que no existe (para fabricar un huérfano en la prueba del barrido).</summary>
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    public static int FindDeadPid()
    {
        int start = OperatingSystem.IsMacOS() ? 99990 : 4194300; // pid_max típico: 99999 en macOS, 4194304 en Linux.
        for (int pid = start; pid > 1000; pid--)
            if (!PidAlive(pid)) return pid;
        return -1;
    }

    /// <summary><c>kill(pid, 0)</c>: existe si devuelve 0 o EPERM; en Linux un zombi cuenta como muerto.</summary>
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    public static bool PidAlive(int pid)
    {
        if (Posix.Kill(pid, 0) == 0) return true;
        int err = Posix.Errno();
        if (err == Posix.ESRCH) return false;
        if (err == Posix.EPERM) return true;
        return false;
    }

    /// <summary>En Linux, si el proceso es un zombi (padre muerto, aún sin recoger) cuenta como desaparecido.</summary>
    [SupportedOSPlatform("linux")]
    public static bool PidIsZombie(int pid)
    {
        try
        {
            string stat = File.ReadAllText($"/proc/{pid}/stat");
            int close = stat.LastIndexOf(')');
            return close >= 0 && close + 2 < stat.Length && stat[close + 2] == 'Z';
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// PID y UID del otro extremo del socket aceptado, como hace <c>GetNamedPipeClientProcessId</c> en Windows.
    /// En Linux <c>SO_PEERCRED</c> devuelve ambos de una vez; en macOS el PID sale de <c>LOCAL_PEERPID</c> y el UID
    /// de <c>LOCAL_PEERCRED</c> (struct xucred).
    /// </summary>
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    public static unsafe (int pid, uint uid) PeerCreds(Socket socket)
    {
        int fd = (int)socket.Handle;
        if (OperatingSystem.IsLinux())
        {
            Span<byte> cred = stackalloc byte[12]; // struct ucred { pid, uid, gid }
            uint len = 12;
            fixed (byte* p = cred)
            {
                if (Posix.GetSockOpt(fd, Posix.SOL_SOCKET_LINUX, Posix.SO_PEERCRED, p, &len) != 0 || len < 12)
                    throw new ModuleProtocolException($"SO_PEERCRED falló (errno {Posix.Errno()}).");
            }
            return (BitConverter.ToInt32(cred), BitConverter.ToUInt32(cred[4..]));
        }
        else
        {
            Span<byte> pidBuf = stackalloc byte[4];
            uint len = 4;
            fixed (byte* p = pidBuf)
            {
                if (Posix.GetSockOpt(fd, Posix.SOL_LOCAL_MAC, Posix.LOCAL_PEERPID, p, &len) != 0 || len < 4)
                    throw new ModuleProtocolException($"LOCAL_PEERPID falló (errno {Posix.Errno()}).");
            }
            int pid = BitConverter.ToInt32(pidBuf);

            Span<byte> xucred = stackalloc byte[76]; // struct xucred: cr_version @0, cr_uid @4
            len = 76;
            fixed (byte* p = xucred)
            {
                if (Posix.GetSockOpt(fd, Posix.SOL_LOCAL_MAC, Posix.LOCAL_PEERCRED, p, &len) != 0 || len < 8)
                    throw new ModuleProtocolException($"LOCAL_PEERCRED falló (errno {Posix.Errno()}).");
            }
            return (pid, BitConverter.ToUInt32(xucred[4..]));
        }
    }
}
