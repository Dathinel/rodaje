using System.Runtime.InteropServices;

namespace Rodaje.App.Services.Crashes;

/// <summary>
/// El nombre largo de una ruta 8.3 (<c>C:\Users\ANAPR~1\AppData\Local\Temp</c> → <c>C:\Users\Ana Pérez\AppData\Local\Temp</c>)
/// con <c>GetLongPathNameW</c>, para que <see cref="CrashReportScrubber"/> reconozca <c>%TEMP%</c> en las dos formas. La
/// ruta debe existir. Fuera de Windows, o si no se puede expandir, devuelve la entrada. P/Invoke con
/// <c>LibraryImport</c>: compatible con Native AOT.
/// </summary>
internal static unsafe partial class CrashLongPathName
{
    /// <summary>La ruta en su nombre largo, o <paramref name="path"/> tal cual si no se puede expandir.</summary>
    public static string Expand(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!OperatingSystem.IsWindows() || path.Length == 0 || !path.Contains('~', StringComparison.Ordinal))
        {
            return path;
        }

        try
        {
            // Primero con un búfer de MAX_PATH; si no alcanza, la función devuelve el largo necesario (con el nulo).
            uint capacity = 260;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                char[] buffer = new char[capacity];
                uint length;
                fixed (char* output = buffer)
                {
                    length = GetLongPathNameW(path, output, capacity);
                }

                if (length == 0)
                {
                    return path;
                }

                if (length < capacity)
                {
                    return new string(buffer, 0, (int)length);
                }

                capacity = length;
            }
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }

        return path;
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint GetLongPathNameW(string shortPath, char* longPath, uint capacity);
}
