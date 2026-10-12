using System.Runtime.InteropServices;

namespace Rodaje.Launcher;

/// <summary>
/// Abre <c>app\Rodaje.exe</c> con la misma línea de comandos, la misma carpeta de trabajo y el mismo modo de ventana
/// (por ejemplo, maximizada desde un acceso directo), y sale sin esperar. Oculta <c>app\</c> la primera vez: quien
/// descomprime Rodaje ve solo este ejecutable.
/// </summary>
internal static unsafe partial class Program
{
    private const string AppFolder = "app";
    private const uint StartfUseShowWindow = 0x1;

    private static int Main()
    {
        string folder = Path.Combine(AppContext.BaseDirectory, AppFolder);
        string exe = Path.Combine(folder, "Rodaje.exe");
        if (!File.Exists(exe))
        {
            _ = MessageBoxW(0, $"No encuentro {exe}.\n\nVuelve a descomprimir Rodaje completo: la carpeta «{AppFolder}» tiene que quedar junto a este Rodaje.exe (puede estar oculta).",
                "Rodaje", 0x10);
            return 2;
        }

        HideOnce(folder);

        STARTUPINFOW own;
        GetStartupInfoW(&own);
        var startup = new STARTUPINFOW { cb = (uint)sizeof(STARTUPINFOW) };
        if ((own.dwFlags & StartfUseShowWindow) != 0)
        {
            startup.dwFlags = StartfUseShowWindow;
            startup.wShowWindow = own.wShowWindow;
        }

        // La ventana de Rodaje puede pasar al frente aunque este proceso ya haya salido.
        _ = AllowSetForegroundWindow(unchecked((uint)-1));
        string commandLine = "\"" + exe + "\"" + ArgumentsOf(new string(GetCommandLineW()));
        PROCESS_INFORMATION process;
        fixed (char* line = commandLine)
        {
            if (CreateProcessW(null, line, 0, 0, 0, 0, 0, null, &startup, &process) == 0)
            {
                int error = Marshal.GetLastPInvokeError();
                _ = MessageBoxW(0, $"No se pudo abrir {exe} (error {error}).", "Rodaje", 0x10);
                return 3;
            }
        }

        _ = CloseHandle(process.hThread);
        _ = CloseHandle(process.hProcess);
        return 0;
    }

    /// <summary>Lo que sigue al nombre del programa, con las reglas de <c>CommandLineToArgvW</c> para el primer argumento.</summary>
    internal static string ArgumentsOf(string commandLine)
    {
        int i = 0;
        if (commandLine.Length > 0 && commandLine[0] == '"')
        {
            int close = commandLine.IndexOf('"', 1);
            i = close < 0 ? commandLine.Length : close + 1;
        }
        else
        {
            while (i < commandLine.Length && commandLine[i] is not (' ' or '\t'))
            {
                i++;
            }
        }

        string rest = commandLine[i..].TrimStart(' ', '\t');
        return rest.Length == 0 ? "" : " " + rest;
    }

    private static void HideOnce(string folder)
    {
        try
        {
            FileAttributes attributes = File.GetAttributes(folder);
            if ((attributes & FileAttributes.Hidden) == 0)
            {
                File.SetAttributes(folder, attributes | FileAttributes.Hidden);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOW
    {
        public uint cb;
        public char* lpReserved;
        public char* lpDesktop;
        public char* lpTitle;
        public uint dwX;
        public uint dwY;
        public uint dwXSize;
        public uint dwYSize;
        public uint dwXCountChars;
        public uint dwYCountChars;
        public uint dwFillAttribute;
        public uint dwFlags;
        public ushort wShowWindow;
        public ushort cbReserved2;
        public byte* lpReserved2;
        public nint hStdInput;
        public nint hStdOutput;
        public nint hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public nint hProcess;
        public nint hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }

    [LibraryImport("kernel32.dll")]
    private static partial char* GetCommandLineW();

    [LibraryImport("kernel32.dll")]
    private static partial void GetStartupInfoW(STARTUPINFOW* info);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CreateProcessW(string? application, char* commandLine, nint processAttributes, nint threadAttributes,
        int inheritHandles, uint flags, nint environment, string? currentDirectory, STARTUPINFOW* startup, PROCESS_INFORMATION* process);

    [LibraryImport("kernel32.dll")]
    private static partial int CloseHandle(nint handle);

    [LibraryImport("user32.dll")]
    private static partial int AllowSetForegroundWindow(uint processId);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBoxW(nint owner, string text, string caption, uint type);
}
