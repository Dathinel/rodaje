using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Rodaje.App.Services.Crashes;

/// <summary>
/// Último filtro de excepciones nativas (<c>SetUnhandledExceptionFilter</c>): ante un fallo nativo fatal escribe un
/// minivolcado (<c>MiniDumpWriteDump</c> de dbghelp) y el informe, y deja seguir al filtro anterior (el del runtime o
/// el de Windows). P/Invoke con <c>LibraryImport</c> y puntero a función: compatible con Native AOT.
/// No ve lo que no pasa por los filtros (<c>RaiseFailFastException</c>, que usan los errores «stowed» de WinRT,
/// o un desbordamiento de pila): eso lo cubre la copia en disco del registro (<see cref="CrashReporter.RecoverUncleanSessions"/>).
/// </summary>
public static unsafe partial class NativeCrashHandler
{
    private const int ExceptionContinueSearch = 0;

    // MiniDumpWithUnloadedModules | MiniDumpWithIndirectlyReferencedMemory | MiniDumpWithThreadInfo: pilas y lo que
    // apuntan, unos pocos MB.
    private const uint DumpType = 0x0020 | 0x0040 | 0x1000;

    private static CrashReporter? s_reporter;
    private static nint s_previous;
    private static int s_entered;

    /// <summary>Instala el filtro (una vez por proceso; en otro sistema que no sea Windows no hace nada).</summary>
    public static void Install(CrashReporter reporter)
    {
        ArgumentNullException.ThrowIfNull(reporter);
        if (!OperatingSystem.IsWindows() || s_reporter is not null)
        {
            return;
        }

        s_reporter = reporter;
        s_previous = SetUnhandledExceptionFilter((nint)(delegate* unmanaged<ExceptionPointers*, int>)&OnUnhandledException);
    }

    /// <summary>
    /// Escribe un minivolcado de este proceso en <paramref name="path"/>; con <paramref name="exceptionPointers"/>
    /// (un <c>EXCEPTION_POINTERS*</c>) lleva la excepción y el hilo que falló. Nunca lanza.
    /// </summary>
    public static bool WriteMinidump(string path, void* exceptionPointers)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using var file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            var info = new MinidumpExceptionInformation
            {
                ThreadId = GetCurrentThreadId(),
                ExceptionPointers = exceptionPointers,
                ClientPointers = 0,
            };
            bool ok = MiniDumpWriteDump(GetCurrentProcess(), (uint)Environment.ProcessId, file.SafeFileHandle, DumpType,
                exceptionPointers is null ? null : &info, 0, 0);
            if (!ok)
            {
                file.Close();
                File.Delete(path);
            }

            return ok;
        }
#pragma warning disable CA1031 // En medio de un fallo nada puede lanzar: sin minivolcado, queda el informe de texto.
        catch (Exception)
#pragma warning restore CA1031
        {
            return false;
        }
    }

    [UnmanagedCallersOnly]
    private static int OnUnhandledException(ExceptionPointers* pointers)
    {
        if (Interlocked.Exchange(ref s_entered, 1) == 0)
        {
            try
            {
                uint code = pointers is not null && pointers->ExceptionRecord is not null ? pointers->ExceptionRecord->ExceptionCode : 0;
                s_reporter?.WriteNativeFatal(code, pointers);
            }
#pragma warning disable CA1031 // Un filtro de excepciones nunca deja escapar otra.
            catch (Exception)
#pragma warning restore CA1031
            {
            }
        }

        return s_previous != 0 ? ((delegate* unmanaged<ExceptionPointers*, int>)s_previous)(pointers) : ExceptionContinueSearch;
    }

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint SetUnhandledExceptionFilter(nint filter);

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint GetCurrentThreadId();

    [LibraryImport("dbghelp.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MiniDumpWriteDump(nint process, uint processId, SafeFileHandle file, uint dumpType,
        MinidumpExceptionInformation* exceptionParam, nint userStreamParam, nint callbackParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct ExceptionRecord
    {
        public uint ExceptionCode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExceptionPointers
    {
        public ExceptionRecord* ExceptionRecord;
        public void* ContextRecord;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct MinidumpExceptionInformation
    {
        public uint ThreadId;
        public void* ExceptionPointers;
        public int ClientPointers;
    }
}
