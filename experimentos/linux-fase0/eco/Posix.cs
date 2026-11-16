using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Rodaje.Experimentos.Eco;

/// <summary>
/// Llamadas POSIX del prototipo: solo se usan en Linux y macOS (en Windows corre <c>--solo-socket</c>,
/// que no las toca). Cada constante lleva su origen en el comentario porque cambian entre los dos sistemas.
/// </summary>
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
internal static partial class Posix
{
    // open(2): O_RDWR es 0x0002 en los dos (asm-generic/fcntl.h y XNU sys/fcntl.h).
    public const int O_RDWR = 0x0002;

    // Banderas de open(2), distintas entre Linux y macOS.
    public const int O_CREAT_LINUX = 0x0040;     // Linux asm-generic/fcntl.h: O_CREAT = 0100
    public const int O_CREAT_MAC = 0x0200;       // XNU sys/fcntl.h: O_CREAT = 0x0200
    public const int O_EXCL_LINUX = 0x0080;      // Linux asm-generic/fcntl.h: O_EXCL = 0200
    public const int O_EXCL_MAC = 0x0800;        // XNU sys/fcntl.h: O_EXCL = 0x0800
    public const int O_NOFOLLOW_LINUX = 0x20000; // Linux asm-generic/fcntl.h: O_NOFOLLOW = 0400000
    public const int O_NOFOLLOW_MAC = 0x0100;    // XNU sys/fcntl.h: O_NOFOLLOW = 0x0100
    public const int O_CLOEXEC_LINUX = 0x80000;  // Linux asm-generic/fcntl.h: O_CLOEXEC = 02000000
    public const int O_CLOEXEC_MAC = 0x1000000;  // XNU sys/fcntl.h: O_CLOEXEC = 0x1000000

    public static int O_CREAT => OperatingSystem.IsMacOS() ? O_CREAT_MAC : O_CREAT_LINUX;
    public static int O_EXCL => OperatingSystem.IsMacOS() ? O_EXCL_MAC : O_EXCL_LINUX;
    public static int O_NOFOLLOW => OperatingSystem.IsMacOS() ? O_NOFOLLOW_MAC : O_NOFOLLOW_LINUX;
    public static int O_CLOEXEC => OperatingSystem.IsMacOS() ? O_CLOEXEC_MAC : O_CLOEXEC_LINUX;

    // errno: solo los que se distinguen en la lógica; el resto solo se imprime.
    public const int EPERM = 1;        // Igual en los dos (errno-base.h / sys/errno.h).
    public const int ENOENT = 2;       // Igual en los dos.
    public const int ESRCH = 3;        // Igual en los dos.
    public const int EINTR = 4;        // Igual en los dos.
    public const int EEXIST = 17;      // Igual en los dos.
    public const int ELOOP_LINUX = 40; // Linux asm-generic/errno.h
    public const int ELOOP_MAC = 62;   // XNU sys/errno.h
    public static int ELOOP => OperatingSystem.IsMacOS() ? ELOOP_MAC : ELOOP_LINUX;

    public const int SIGKILL = 9; // Igual en los dos.

    public const int PR_SET_PDEATHSIG = 1; // Linux include/uapi/linux/prctl.h (solo Linux).

    // Credenciales del otro extremo del socket Unix.
    public const int SOL_SOCKET_LINUX = 1;   // Linux asm-generic/socket.h
    public const int SO_PEERCRED = 17;       // Linux asm-generic/socket.h → struct ucred { pid, uid, gid } (3 × int32)
    public const int SOL_LOCAL_MAC = 0;      // XNU sys/socket.h: SOL_LOCAL = 0
    public const int LOCAL_PEERCRED = 0x001; // XNU sys/un.h → struct xucred: cr_version @0, cr_uid @4 (XNU sys/ucred.h)
    public const int LOCAL_PEERPID = 0x002;  // XNU sys/un.h → pid_t

    // Desplazamientos dentro de struct stat, para leer solo lo necesario sin depender de la forma completa.
    // Linux x86-64 (arch/x86/include/uapi/asm/stat.h, igual en glibc): st_mode @24 (u32), st_uid @28 (u32).
    public const int STAT_MODE_LINUX = 24;
    public const int STAT_UID_LINUX = 28;
    // macOS arm64 (XNU bsd/sys/stat.h, la variante de 64 bits): st_mode @4 (u16), st_uid @16 (u32).
    public const int STAT_MODE_MAC = 4;
    public const int STAT_UID_MAC = 16;
    public const int STAT_BUF = 256;        // Basta y sobra para struct stat en los dos.
    public const uint S_IFMT = 0xF000;      // Máscara del tipo de archivo, igual en los dos.
    public const uint S_IFREG = 0x8000;     // Archivo regular, igual en los dos.

    // kqueue (solo macOS, XNU sys/event.h).
    public const short EVFILT_PROC = -5;
    public const ushort EV_ADD = 0x0001;
    public const uint NOTE_EXIT = 0x80000000;

    public static int Errno() => Marshal.GetLastWin32Error();

    // open(path, flags) sin modo: seguro en la ABI variádica de arm64 de Apple (no hay argumento tras
    // los fijos). La creación con modo 0600 va por FileStreamOptions.UnixCreateMode (API de .NET).
    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int Open(string path, int flags);

    [LibraryImport("libc", EntryPoint = "fstat", SetLastError = true)]
    public static unsafe partial int FStat(int fd, byte* buf);

    [LibraryImport("libc", EntryPoint = "getsockopt", SetLastError = true)]
    public static unsafe partial int GetSockOpt(int fd, int level, int name, byte* value, uint* length);

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    public static partial int Kill(int pid, int sig);

    [LibraryImport("libc", EntryPoint = "close")]
    public static partial int Close(int fd);

    [LibraryImport("libc", EntryPoint = "geteuid")]
    public static partial uint GetEuid();

    [LibraryImport("libc", EntryPoint = "getppid")]
    public static partial int GetPPid();

    [LibraryImport("libc", EntryPoint = "prctl", SetLastError = true)]
    [SupportedOSPlatform("linux")]
    public static partial int PrCtl(int option, ulong arg2, ulong arg3, ulong arg4, ulong arg5);

    [LibraryImport("libc", EntryPoint = "kqueue", SetLastError = true)]
    [SupportedOSPlatform("macos")]
    public static partial int KQueue();

    [LibraryImport("libc", EntryPoint = "kevent", SetLastError = true)]
    [SupportedOSPlatform("macos")]
    public static unsafe partial int KEvent(int kq, KEvent* changelist, int nchanges, KEvent* eventlist, int nevents, void* timeout);
}

/// <summary>struct kevent de macOS arm64: ident, filter, flags, fflags, data, udata (32 B).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct KEvent
{
    public nuint Ident;
    public short Filter;
    public ushort Flags;
    public uint Fflags;
    public nint Data;
    public nint Udata;
}
