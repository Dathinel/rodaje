using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Rodaje.App.Services.Crashes;

/// <summary>
/// Último filtro de excepciones nativas (<c>SetUnhandledExceptionFilter</c>): ante un fallo nativo fatal escribe un
/// minivolcado (<c>MiniDumpWriteDump</c> de dbghelp) y el informe, y deja seguir al filtro anterior (el del runtime o
/// el de Windows). Un <see cref="CrashWatchdog"/> termina el proceso si eso tarda más de 10 s. P/Invoke con <c>LibraryImport</c> y puntero a función: compatible con Native AOT.
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
    private static CrashWatchdog? s_watchdog;
    private static int s_entered;

    // ERROR_NOT_SUPPORTED: fuera de Windows no hay minivolcado.
    private const int NotSupportedError = 50;

    /// <summary>Instala el filtro (una vez por proceso; en otro sistema que no sea Windows no hace nada).</summary>
    public static void Install(CrashReporter reporter)
    {
        ArgumentNullException.ThrowIfNull(reporter);
        if (!OperatingSystem.IsWindows() || s_reporter is not null)
        {
            return;
        }

        s_reporter = reporter;
        s_watchdog = new CrashWatchdog(CrashWatchdog.DefaultTimeout, static code => TerminateProcess(GetCurrentProcess(), code));
        s_previous = SetUnhandledExceptionFilter((nint)(delegate* unmanaged<ExceptionPointers*, int>)&OnUnhandledException);
    }

    /// <summary>
    /// Escribe un minivolcado de este proceso en <paramref name="path"/>; con <paramref name="exceptionPointers"/>
    /// (un <c>EXCEPTION_POINTERS*</c>) lleva la excepción y el hilo que falló. Nunca lanza. Volcarse a sí mismo puede
    /// trabarse si otro hilo tiene el cargador o el montón: vale en un proceso que ya se cae, no en uno sano. Si falla,
    /// <paramref name="error"/> es el error de Win32 (o el HRESULT de la excepción) y no queda archivo.
    /// </summary>
    public static bool WriteMinidump(string path, void* exceptionPointers, out int error)
    {
        if (!OperatingSystem.IsWindows())
        {
            error = NotSupportedError;
            return false;
        }

        var info = new MinidumpExceptionInformation
        {
            ThreadId = GetCurrentThreadId(),
            ExceptionPointers = exceptionPointers,
            ClientPointers = 0,
        };
        return WriteMinidump(path, GetCurrentProcess(), (uint)Environment.ProcessId, exceptionPointers is null ? null : &info, out error);
    }

    /// <summary>Escribe un minivolcado de otro proceso (sin excepción). Nunca lanza; si falla, <paramref name="error"/> dice por qué.</summary>
    public static bool WriteMinidump(string path, System.Diagnostics.Process process, out int error)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (!OperatingSystem.IsWindows())
        {
            error = NotSupportedError;
            return false;
        }

        try
        {
            return WriteMinidump(path, process.Handle, (uint)process.Id, null, out error);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            error = ex.HResult;
            return false;
        }
    }

    /// <summary>
    /// Lo que hace el filtro ante un fallo: informe de texto y minivolcado (<see cref="CrashReporter.WriteNativeFatal"/>)
    /// con el vigilante armado, que termina el proceso si tardan más de su tiempo. Nunca lanza.
    /// </summary>
    public static string? WriteGuarded(CrashReporter reporter, CrashWatchdog? watchdog, uint exceptionCode, void* exceptionPointers)
    {
        ArgumentNullException.ThrowIfNull(reporter);
        watchdog?.Arm(exceptionCode == 0 ? 1u : exceptionCode);
        try
        {
            return reporter.WriteNativeFatal(exceptionCode, exceptionPointers);
        }
#pragma warning disable CA1031 // Un filtro de excepciones nunca deja escapar otra.
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
        finally
        {
            watchdog?.Disarm();
        }
    }

    private static bool WriteMinidump(string path, nint process, uint processId, MinidumpExceptionInformation* info, out int error)
    {
        error = 0;
        try
        {
            using var file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            bool ok = MiniDumpWriteDump(process, processId, file.SafeFileHandle, DumpType, info, 0, 0);
            if (!ok)
            {
                // MiniDumpWriteDump devuelve un HRESULT en GetLastError; sin él, un código distinto de 0 igual.
                error = Marshal.GetLastPInvokeError() is int last and not 0 ? last : -1;
                file.Close();
                File.Delete(path);
            }

            return ok;
        }
#pragma warning disable CA1031 // En medio de un fallo nada puede lanzar: sin minivolcado, queda el informe de texto.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            error = ex.HResult is int code and not 0 ? code : -1;
            return false;
        }
    }

    [UnmanagedCallersOnly]
    private static int OnUnhandledException(ExceptionPointers* pointers)
    {
        if (Interlocked.Exchange(ref s_entered, 1) == 0 && s_reporter is { } reporter)
        {
            uint code = pointers is not null && pointers->ExceptionRecord is not null ? pointers->ExceptionRecord->ExceptionCode : 0;
            _ = WriteGuarded(reporter, s_watchdog, code, pointers);
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

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateProcess(nint process, uint exitCode);

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
