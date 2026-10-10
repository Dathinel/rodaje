namespace Rodaje.App.Services;

/// <summary>
/// Cuántas instancias de Rodaje hay abiertas. Cada una tiene abierto sin compartir un archivo
/// <c>instancias\&lt;pid&gt;-&lt;aleatorio&gt;.lock</c> que el sistema borra al cerrarse el proceso, también si se cierra de
/// golpe. Un archivo que se puede abrir es de un proceso que ya no existe (por ejemplo, tras un apagón): no cuenta y se borra.
/// </summary>
public sealed partial class InstanceRegistry : IDisposable
{
    public const string Extension = ".lock";

    private readonly FileStream? _lock;

    private InstanceRegistry(string directory, FileStream? lockFile)
    {
        Directory = directory;
        _lock = lockFile;
    }

    /// <summary><c>%LOCALAPPDATA%\Rodaje\instancias</c>.</summary>
    public static string DefaultDirectory => Path.Combine(AppDataPaths.Root, "instancias");

    public string Directory { get; }

    /// <summary>Si no se pudo crear su archivo, esta instancia se cuenta igual pero las demás no la ven.</summary>
    public bool IsRegistered => _lock is not null;

    /// <summary>Anota esta instancia. Nunca falla: si el disco no deja, queda sin anotar.</summary>
    public static InstanceRegistry Register(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        string full = Path.GetFullPath(directory);
        try
        {
            System.IO.Directory.CreateDirectory(full);
            string path = Path.Combine(full, $"{Environment.ProcessId}-{Guid.NewGuid().ToString("N")[..8]}{Extension}");
            return new InstanceRegistry(full, new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new InstanceRegistry(full, null);
        }
    }

    /// <summary>Instancias activas contando esta; al menos 1.</summary>
    public int CountActive() => Math.Max(1, CountRegistered(Directory) + (IsRegistered ? 0 : 1));

    /// <summary>Archivos de <paramref name="directory"/> que tiene abiertos un proceso vivo. Borra los de procesos muertos.</summary>
    public static int CountRegistered(string directory)
    {
        IEnumerable<string> files;
        try
        {
            files = [.. System.IO.Directory.EnumerateFiles(directory, "*" + Extension)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }

        int count = 0;
        foreach (string file in files)
        {
            try
            {
                using var stale = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            catch (FileNotFoundException)
            {
            }
            catch (UnauthorizedAccessException)
            {
                // Se está borrando: su instancia acaba de cerrarse.
            }
            catch (IOException)
            {
                count++;
            }
        }

        return count;
    }

    public void Dispose() => _lock?.Dispose();
}
