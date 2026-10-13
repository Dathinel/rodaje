using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Rodaje.App.Tests.Leaks;

/// <summary>
/// Los identificadores del proceso por tipo, para las pruebas de fugas. En Windows, <see cref="Process.HandleCount"/>
/// suma también los de cada hilo (el del hilo y los eventos que el runtime le crea), y el grupo de hilos crea y suelta
/// hilos por su cuenta: en la CI llegó a sumar 108 en 40 aperturas y luego nada en 50 (806 837 867 897 913 913 914…),
/// y en otra corrida osciló 746 747 746 747…; ninguna de las dos es una fuga de la sesión. Aquí se cuentan aparte los
/// tipos que nacen con los hilos (<see cref="ThreadTypes"/>) y los hilos que no son del grupo, que es donde se vería un
/// hilo propio sin cerrar.
/// </summary>
internal static partial class ProcessHandles
{
    /// <summary>
    /// Tipos de objeto que el runtime crea con cada hilo en Windows: el hilo mismo y sus eventos de espera y suspensión.
    /// En Linux un hilo no abre descriptores, así que no hay nada que apartar.
    /// </summary>
    public static readonly IReadOnlySet<string> ThreadTypes = new HashSet<string>(StringComparer.Ordinal) { "Thread", "Event" };

    private const int SystemExtendedHandleInformation = 64;
    private const int ObjectTypeInformation = 2;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);

    /// <summary>Tamaño de <c>SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX</c> en 64 bits y dónde va cada campo que se lee.</summary>
    private const int EntrySize = 40;
    private const int EntriesOffset = 16;
    private const int ProcessIdOffset = 8;
    private const int HandleValueOffset = 16;
    private const int TypeIndexOffset = 30;

    private static readonly Dictionary<ushort, string> TypeNames = [];

    /// <summary>Lo que hay ahora, después de juntar la basura y correr los finalizadores.</summary>
    public static Snapshot Take()
    {
        // Lo que solo espera a su finalizador (un SafeHandle abandonado) no es una fuga: se cierra antes de contar.
        for (int i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        int poolBefore = ThreadPool.ThreadCount;
        using var process = Process.GetCurrentProcess();
        int total = process.HandleCount;
        int threads = process.Threads.Count;
        int pool = Math.Max(poolBefore, ThreadPool.ThreadCount);
        IReadOnlyDictionary<string, int>? byType = OperatingSystem.IsWindows() && Environment.Is64BitProcess ? WindowsByType()
            : OperatingSystem.IsLinux() ? LinuxByType()
            : null;
        return new Snapshot(total, byType, threads, pool);
    }

    /// <summary>Una muestra: el total, el reparto por tipo si se pudo leer, y los hilos del proceso y del grupo.</summary>
    public sealed record Snapshot(int Total, IReadOnlyDictionary<string, int>? ByType, int Threads, int PoolThreads)
    {
        /// <summary>
        /// Los identificadores que no nacen con un hilo: archivos, carpetas vigiladas, secciones de memoria, claves,
        /// procesos, semáforos… Sin reparto por tipo, el total.
        /// </summary>
        public int Owned => ByType is null ? Total : ByType.Where(t => !ThreadTypes.Contains(t.Key)).Sum(t => t.Value);

        /// <summary>Hilos que no son del grupo de hilos: un hilo dedicado sin cerrar los hace crecer.</summary>
        public int OwnThreads => Threads - PoolThreads;

        public int Of(string type) => ByType is not null && ByType.TryGetValue(type, out int count) ? count : 0;
    }

    /// <summary>Lo que cambió cada tipo entre dos muestras, para el mensaje de la prueba.</summary>
    public static string Changes(Snapshot first, Snapshot last)
    {
        if (first.ByType is null || last.ByType is null)
        {
            return $"total {first.Total} → {last.Total}";
        }

        IEnumerable<string> changed = first.ByType.Keys.Union(last.ByType.Keys)
            .Select(type => (type, delta: last.Of(type) - first.Of(type)))
            .Where(t => t.delta != 0)
            .OrderBy(t => t.type, StringComparer.Ordinal)
            .Select(t => $"{t.type} {t.delta:+0;-0}");
        string text = string.Join(", ", changed);
        return text.Length == 0 ? "ningún tipo cambió" : text;
    }

    /// <summary>Descriptores de <c>/proc/self/fd</c>, por lo que señalan (archivo, tubería, socket, eventfd…).</summary>
    private static Dictionary<string, int>? LinuxByType()
    {
        try
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (FileSystemInfo fd in new DirectoryInfo("/proc/self/fd").EnumerateFileSystemInfos())
            {
                string? target = fd.LinkTarget;
                string type = target switch
                {
                    null => "?",
                    _ when target.StartsWith('/') => "File",
                    _ when target.StartsWith("anon_inode:", StringComparison.Ordinal) => target["anon_inode:".Length..].Trim('[', ']'),
                    _ when target.Contains(':', StringComparison.Ordinal) => target[..target.IndexOf(':', StringComparison.Ordinal)],
                    _ => target,
                };
                counts[type] = counts.GetValueOrDefault(type) + 1;
            }

            return counts;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Los identificadores de este proceso en la tabla del sistema (<c>NtQuerySystemInformation</c>), por tipo. El nombre
    /// de cada tipo sale de <c>NtQueryObject</c> sobre uno de sus identificadores, que son de este proceso.
    /// </summary>
    private static Dictionary<string, int>? WindowsByType()
    {
        int size = 4 << 20;
        for (int attempt = 0; attempt < 8; attempt++)
        {
            nint buffer = Marshal.AllocHGlobal(size);
            try
            {
                int status = NtQuerySystemInformation(SystemExtendedHandleInformation, buffer, size, out int needed);
                if (status == StatusInfoLengthMismatch)
                {
                    size = Math.Max(size * 2, needed + (1 << 20));
                    continue;
                }

                if (status != 0)
                {
                    return null;
                }

                long count = Marshal.ReadInt64(buffer);
                long self = Environment.ProcessId;
                var counts = new Dictionary<string, int>(StringComparer.Ordinal);
                for (long i = 0; i < count; i++)
                {
                    nint entry = buffer + EntriesOffset + (nint)(i * EntrySize);
                    if (Marshal.ReadInt64(entry, ProcessIdOffset) != self)
                    {
                        continue;
                    }

                    ushort index = unchecked((ushort)Marshal.ReadInt16(entry, TypeIndexOffset));
                    string type = TypeName(index, Marshal.ReadIntPtr(entry, HandleValueOffset));
                    counts[type] = counts.GetValueOrDefault(type) + 1;
                }

                return counts;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return null;
    }

    private static string TypeName(ushort index, nint handle)
    {
        lock (TypeNames)
        {
            if (TypeNames.TryGetValue(index, out string? known))
            {
                return known;
            }

            const int Size = 4096;
            nint info = Marshal.AllocHGlobal(Size);
            try
            {
                // PUBLIC_OBJECT_TYPE_INFORMATION empieza por el UNICODE_STRING del nombre: longitud en bytes y puntero.
                if (NtQueryObject(handle, ObjectTypeInformation, info, Size, out _) != 0)
                {
                    return $"tipo {index}";
                }

                int length = unchecked((ushort)Marshal.ReadInt16(info));
                nint text = Marshal.ReadIntPtr(info, 8);
                string name = text == 0 ? $"tipo {index}" : Marshal.PtrToStringUni(text, length / 2);
                TypeNames[index] = name;
                return name;
            }
            finally
            {
                Marshal.FreeHGlobal(info);
            }
        }
    }

    [LibraryImport("ntdll.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int NtQuerySystemInformation(int informationClass, nint buffer, int length, out int returnLength);

    [LibraryImport("ntdll.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int NtQueryObject(nint handle, int informationClass, nint buffer, int length, out int returnLength);
}
