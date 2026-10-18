using Rodaje.App.Services.Settings;

namespace Rodaje.App.Services.Media;

/// <summary>Una carpeta vigilada: la de la cámara, la que sincroniza el teléfono…</summary>
public sealed class WatchedFolder
{
    public string Path { get; set; } = "";

    /// <summary>Desde cuándo se vigila: lo que ya estaba antes no cuenta como nuevo.</summary>
    public DateTime SinceUtc { get; set; }

    public string Name => System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(Path)) is { Length: > 0 } name ? name : Path;
}

/// <summary>Un archivo nuevo en una carpeta vigilada.</summary>
public sealed record WatchedFile(string Path, string FolderName, long Size);

/// <summary>
/// Busca medios nuevos en las carpetas vigiladas (<see cref="AppSettings.WatchedFolders"/>): con la extensión de un video o un
/// audio, creados o escritos después de empezar a vigilar, que no están en la biblioteca ni se importaron o descartaron
/// antes, y que ya terminaron de copiarse (el mismo tamaño en dos pasadas seguidas). Mira hasta tres niveles de subcarpetas
/// (DCIM\100CANON\…).
/// </summary>
public sealed class WatchedFolderScanner
{
    public const int MaxDepth = 3;

    public const int MaxHandled = 2000;

    public static readonly IReadOnlySet<string> MediaExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".mov", ".m4v", ".webm", ".avi", ".mts", ".m2ts", ".ts", ".wmv", ".flv", ".3gp",
        ".mp3", ".wav", ".m4a", ".aac", ".flac", ".ogg", ".opus",
        ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".tif", ".tiff",
    };

    private readonly Dictionary<string, long> _sizes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Lo nuevo y ya completo de todas las carpetas. Se llama fuera del hilo de la interfaz.</summary>
    public IReadOnlyList<WatchedFile> Scan(IReadOnlyList<WatchedFolder> folders, IReadOnlyCollection<string> handled, Func<string, bool> inLibrary)
    {
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(handled);
        ArgumentNullException.ThrowIfNull(inLibrary);
        var handledSet = handled as IReadOnlySet<string> ?? new HashSet<string>(handled, StringComparer.OrdinalIgnoreCase);
        var found = new List<WatchedFile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (WatchedFolder folder in folders)
        {
            foreach (FileInfo file in Files(folder.Path))
            {
                string path = file.FullName;
                if (!MediaExtensions.Contains(file.Extension) || handledSet.Contains(path) || !seen.Add(path)
                    || Max(file.CreationTimeUtc, file.LastWriteTimeUtc) < folder.SinceUtc || inLibrary(path))
                {
                    continue;
                }

                long size = file.Length;
                bool stable = size > 0 && _sizes.TryGetValue(path, out long previous) && previous == size;
                _sizes[path] = size;
                if (stable)
                {
                    found.Add(new WatchedFile(path, folder.Name, size));
                }
            }
        }

        foreach (string gone in _sizes.Keys.Where(k => !seen.Contains(k)).ToArray())
        {
            _sizes.Remove(gone);
        }

        return found;
    }

    /// <summary>Anota archivos como ya vistos (importados o descartados) para no volver a ofrecerlos.</summary>
    public static void MarkHandled(AppSettings settings, IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(paths);
        foreach (string path in paths)
        {
            if (!settings.HandledWatchedFiles.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                settings.HandledWatchedFiles.Add(path);
            }
        }

        if (settings.HandledWatchedFiles.Count > MaxHandled)
        {
            settings.HandledWatchedFiles.RemoveRange(0, settings.HandledWatchedFiles.Count - MaxHandled);
        }
    }

    /// <summary>Empieza a vigilar una carpeta (desde ahora); <c>false</c> si ya estaba.</summary>
    public static bool Add(AppSettings settings, string folder, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        string full = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(folder));
        if (settings.WatchedFolders.Exists(f => string.Equals(f.Path, full, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        settings.WatchedFolders.Add(new WatchedFolder { Path = full, SinceUtc = nowUtc });
        return true;
    }

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;

    private static FileInfo[] Files(string root)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true, MaxRecursionDepth = MaxDepth, IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.Temporary,
        };
        try
        {
            return Directory.Exists(root) ? new DirectoryInfo(root).EnumerateFiles("*", options).ToArray() : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return [];
        }
    }
}
