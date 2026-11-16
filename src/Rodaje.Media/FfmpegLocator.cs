namespace Rodaje.Media;

/// <summary>Decide en qué carpeta buscar las DLL de FFmpeg, en un orden fijo y comprobable.</summary>
/// <remarks>
/// Orden: carpeta explícita; <c>RODAJE_FFMPEG</c>; <c>ffmpeg\</c> junto al ejecutable (distribución);
/// <c>RODAJE_ENTORNO\ffmpeg\bin</c>; el primer <c>.entorno\ffmpeg\bin</c> subiendo desde el ejecutable; y,
/// si en ese camino hay un worktree de git, el <c>.entorno</c> de su checkout principal. Si junto al ejecutable
/// existe <c>ffmpeg\</c>, <c>RODAJE_FFMPEG</c> se ignora: la distribución usa siempre el FFmpeg LGPL que trae.
/// </remarks>
public static class FfmpegLocator
{
    /// <summary>DLL que deben estar todas en la carpeta elegida.</summary>
    public static IReadOnlyList<string> RequiredLibraries { get; } =
        ["avutil-61", "swresample-7", "swscale-10", "avcodec-63", "avformat-63"];

    /// <summary>Subcarpeta de <c>.entorno/ffmpeg</c> con las bibliotecas: <c>bin</c> en Windows, <c>lib</c> en Linux y macOS.</summary>
    public static string LibrarySubdirectory => OperatingSystem.IsWindows() ? "bin" : "lib";

    /// <summary>Archivo de la biblioteca <c>&lt;nombre&gt;-&lt;mayor&gt;</c> en este sistema.</summary>
    public static string LibraryFileName(string versioned) => LibraryFileName(versioned, HostOs);

    internal static string LibraryFileName(string versioned, NativeOs os)
    {
        ArgumentNullException.ThrowIfNull(versioned);
        string[] parts = versioned.Split('-', 2);
        if (parts.Length != 2 || !int.TryParse(parts[1], out _))
        {
            return versioned;
        }

        return os switch
        {
            NativeOs.Windows => $"{parts[0]}-{parts[1]}.dll",
            NativeOs.MacOs => $"lib{parts[0]}.{parts[1]}.dylib",
            _ => $"lib{parts[0]}.so.{parts[1]}",
        };
    }

    /// <summary>
    /// El nombre <c>&lt;biblioteca&gt;-&lt;mayor&gt;</c> de un archivo de biblioteca de FFmpeg de cualquier sistema
    /// (<c>avfilter-12.dll</c>, <c>libavfilter.so.12</c>, <c>libavfilter.12.dylib</c>), o <c>null</c> si no lo es.
    /// </summary>
    internal static string? VersionedName(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        string[] parts;
        if (fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            parts = fileName[..^4].Split('-');
        }
        else if (fileName.StartsWith("lib", StringComparison.Ordinal) && fileName.EndsWith(".dylib", StringComparison.Ordinal))
        {
            parts = fileName[3..^6].Split('.');
        }
        else if (fileName.StartsWith("lib", StringComparison.Ordinal) && fileName.Contains(".so.", StringComparison.Ordinal))
        {
            parts = fileName[3..].Split(".so.");
        }
        else
        {
            return null;
        }

        return parts.Length == 2 && parts[0].Length > 0 && int.TryParse(parts[1], out _) ? $"{parts[0]}-{parts[1]}" : null;
    }

    /// <summary>Nombre de un programa de FFmpeg (<c>ffmpeg</c>, <c>ffprobe</c>) en este sistema.</summary>
    public static string ExecutableName(string tool) => OperatingSystem.IsWindows() ? tool + ".exe" : tool;

    /// <summary>
    /// Carpeta de <c>ffmpeg</c> y <c>ffprobe</c> para una carpeta de bibliotecas: la misma en Windows; en Linux y macOS,
    /// <c>../bin</c> si ahí está <c>ffmpeg</c> (<c>.entorno</c>), si no la misma (distribución).
    /// </summary>
    public static string ExecutableDirectory(string libraryDirectory)
    {
        ArgumentNullException.ThrowIfNull(libraryDirectory);
        if (OperatingSystem.IsWindows())
        {
            return libraryDirectory;
        }

        string bin = Path.GetFullPath(Path.Combine(libraryDirectory, "..", "bin"));
        return File.Exists(Path.Combine(bin, ExecutableName("ffmpeg"))) ? bin : libraryDirectory;
    }

    internal static NativeOs HostOs =>
        OperatingSystem.IsWindows() ? NativeOs.Windows : OperatingSystem.IsMacOS() ? NativeOs.MacOs : NativeOs.Linux;

    public static IReadOnlyList<string> Candidates(
        string? explicitDirectory,
        string baseDirectory,
        Func<string, string?> getEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(baseDirectory);
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);
        var result = new List<string>();
        void Add(string? path)
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                string full = Path.GetFullPath(path);
                if (!result.Contains(full, StringComparer.OrdinalIgnoreCase))
                {
                    result.Add(full);
                }
            }
        }

        string bundled = Path.Combine(baseDirectory, "ffmpeg");
        Add(explicitDirectory);
        if (!Directory.Exists(bundled))
        {
            Add(getEnvironmentVariable(EnvironmentVariables.Ffmpeg));
        }

        Add(bundled);
        string? entorno = getEnvironmentVariable(EnvironmentVariables.Entorno);
        if (!string.IsNullOrWhiteSpace(entorno))
        {
            Add(Path.Combine(entorno, "ffmpeg", "bin"));
        }

        for (DirectoryInfo? dir = new(Path.GetFullPath(baseDirectory)); dir is not null; dir = dir.Parent)
        {
            Add(Path.Combine(dir.FullName, ".entorno", "ffmpeg", "bin"));
            string? main = MainCheckoutOfWorktree(dir.FullName);
            if (main is not null)
            {
                Add(Path.Combine(main, ".entorno", "ffmpeg", "bin"));
            }
        }

        return result;
    }

    /// <summary>Primera candidata que contiene todas las DLL requeridas, o <c>null</c>.</summary>
    public static string? Find(IEnumerable<string> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        return candidates.FirstOrDefault(HasAllLibraries);
    }

    public static bool HasAllLibraries(string directory) =>
        RequiredLibraries.All(name => File.Exists(Path.Combine(directory, name + ".dll")));

    /// <summary>
    /// En un worktree, <c>.git</c> es un archivo con <c>gitdir: &lt;principal&gt;/.git/worktrees/&lt;nombre&gt;</c>;
    /// devuelve la carpeta del checkout principal.
    /// </summary>
    public static string? MainCheckoutOfWorktree(string directory)
    {
        string gitFile = Path.Combine(directory, ".git");
        if (!File.Exists(gitFile))
        {
            return null;
        }

        string? line;
        try
        {
            line = File.ReadLines(gitFile).FirstOrDefault();
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        const string Prefix = "gitdir:";
        if (line is null || !line.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string gitDir = line[Prefix.Length..].Trim();
        if (!Path.IsPathFullyQualified(gitDir))
        {
            gitDir = Path.GetFullPath(gitDir, directory);
        }

        // <principal>\.git\worktrees\<nombre>: tres niveles arriba está el checkout principal
        DirectoryInfo? worktreeEntry = new(gitDir);
        DirectoryInfo? dotGit = worktreeEntry.Parent?.Parent;
        if (dotGit is null || !string.Equals(worktreeEntry.Parent!.Name, "worktrees", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(dotGit.Name, ".git", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return dotGit.Parent?.FullName;
    }
}

/// <summary>Sistema para el que se nombra una biblioteca nativa.</summary>
internal enum NativeOs
{
    Windows,
    Linux,
    MacOs,
}
