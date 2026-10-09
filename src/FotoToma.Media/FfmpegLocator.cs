namespace FotoToma.Media;

/// <summary>Decide en qué carpeta buscar las DLL de FFmpeg, en un orden fijo y comprobable.</summary>
/// <remarks>
/// Orden: carpeta explícita; <c>FOTOTOMA_FFMPEG</c>; <c>ffmpeg\</c> junto al ejecutable (distribución);
/// <c>FOTOTOMA_ENTORNO\ffmpeg\bin</c>; el primer <c>.entorno\ffmpeg\bin</c> subiendo desde el ejecutable; y,
/// si en ese camino hay un worktree de git, el <c>.entorno</c> de su checkout principal.
/// </remarks>
public static class FfmpegLocator
{
    /// <summary>DLL que deben estar todas en la carpeta elegida.</summary>
    public static IReadOnlyList<string> RequiredLibraries { get; } =
        ["avutil-61", "swresample-7", "swscale-10", "avcodec-63", "avformat-63"];

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

        Add(explicitDirectory);
        Add(getEnvironmentVariable("FOTOTOMA_FFMPEG"));
        Add(Path.Combine(baseDirectory, "ffmpeg"));
        string? entorno = getEnvironmentVariable("FOTOTOMA_ENTORNO");
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
