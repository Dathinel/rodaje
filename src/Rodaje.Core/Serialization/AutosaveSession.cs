using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Rodaje.Core.Model;

namespace Rodaje.Core.Serialization;

/// <summary>
/// Las copias de autoguardado de una ejecución del editor, en su propia carpeta dentro de la de autoguardado
/// (<c>%LOCALAPPDATA%\Rodaje\Autoguardado\&lt;sesión&gt;</c>): un historial de las últimas versiones del proyecto,
/// un archivo de datos y un archivo de bloqueo.
/// </summary>
/// <remarks>
/// <para>
/// <b>Cierre inesperado.</b> Mientras la ejecución vive, tiene abierto <see cref="LockFileName"/> sin dejar que otro
/// lo abra para escribir. El archivo no se borra al cerrarse el proceso: si el editor se cierra bien llama a
/// <see cref="Complete"/>, que borra la carpeta entera; si se cae o se corta la luz, la carpeta queda con su
/// bloqueo, y como el sistema suelta los archivos de un proceso muerto, el siguiente arranque lo puede abrir y sabe
/// que esa ejecución terminó mal (<see cref="AutosaveRecovery.FindRecoverable"/>).
/// </para>
/// <para>
/// <b>Escritura atómica.</b> Cada copia es un <c>.rodaje</c> nuevo escrito con <see cref="ProjectSerializer.Save(Project, string)"/>
/// (temporal llevado al disco y renombrado), así que en la carpeta solo hay copias completas; el archivo de datos
/// también se escribe a un temporal y se renombra. Se conservan las <see cref="MaxVersions"/> más recientes.
/// </para>
/// <para>Es seguro llamarla desde cualquier hilo: las operaciones van de una en una.</para>
/// </remarks>
public sealed class AutosaveSession : IDisposable
{
    /// <summary>Nombre de la carpeta de autoguardado dentro de la de datos de Rodaje.</summary>
    public const string DefaultFolderName = "Autoguardado";

    /// <summary>Archivo que la ejecución tiene abierto mientras vive.</summary>
    public const string LockFileName = "sesion.lock";

    /// <summary>Datos de la sesión: proyecto, archivo y si hay cambios sin guardar.</summary>
    public const string InfoFileName = "sesion.json";

    /// <summary>Comienzo del nombre de cada copia; le sigue el número de versión con seis cifras.</summary>
    public const string SnapshotPrefix = "copia-";

    /// <summary>Copias que se conservan por omisión.</summary>
    public const int DefaultMaxVersions = 10;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private readonly Lock _gate = new();
    private readonly ProjectSerializer.SaveFileSystem _fileSystem;
    private FileStream? _lock;
    private int _nextNumber;
    private bool _completed;

    private AutosaveSession(string directory, FileStream lockFile, int maxVersions, ProjectSerializer.SaveFileSystem fileSystem)
    {
        Directory = directory;
        _lock = lockFile;
        MaxVersions = maxVersions;
        _fileSystem = fileSystem;
        _nextNumber = 1;
    }

    /// <summary>Carpeta de esta sesión.</summary>
    public string Directory { get; }

    /// <summary>Cuántas copias se conservan; al escribir una más se borra la más antigua.</summary>
    public int MaxVersions { get; }

    /// <summary>
    /// Empieza una sesión nueva en <paramref name="root"/> (la carpeta de autoguardado): crea su carpeta y abre el
    /// archivo de bloqueo.
    /// </summary>
    /// <exception cref="IOException">Si no se pudo crear la carpeta o el bloqueo; sin él no se distinguiría un cierre inesperado.</exception>
    /// <exception cref="UnauthorizedAccessException">Si la carpeta no se puede escribir.</exception>
    public static AutosaveSession Begin(string root, int maxVersions = DefaultMaxVersions) =>
        Begin(root, maxVersions, DateTimeOffset.Now, ProjectSerializer.SaveFileSystem.Real);

    internal static AutosaveSession Begin(string root, int maxVersions, DateTimeOffset now, ProjectSerializer.SaveFileSystem fileSystem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxVersions, 1);
        ArgumentNullException.ThrowIfNull(fileSystem);

        string name = string.Create(CultureInfo.InvariantCulture,
            $"{now.UtcDateTime:yyyyMMdd-HHmmss}-{Environment.ProcessId}-{Guid.NewGuid().ToString("N")[..6]}");
        string directory = Path.Combine(Path.GetFullPath(root), name);
        System.IO.Directory.CreateDirectory(directory);

        // Se escribe y nunca se comparte para escribir: así otro proceso solo lo puede abrir con escritura si este
        // ya no existe. No lleva DeleteOnClose porque Windows también borraría el archivo al caerse el proceso.
        var lockFile = new FileStream(Path.Combine(directory, LockFileName), FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1);
        try
        {
            lockFile.Write(Encoding.ASCII.GetBytes(Environment.ProcessId.ToString(CultureInfo.InvariantCulture)));
            lockFile.Flush(flushToDisk: true);
        }
        catch
        {
            lockFile.Dispose();
            throw;
        }

        return new AutosaveSession(directory, lockFile, maxVersions, fileSystem);
    }

    /// <summary>
    /// Escribe una copia nueva de <paramref name="project"/> y anota que tiene cambios sin guardar. Si se pasa
    /// de <see cref="MaxVersions"/>, borra las más antiguas.
    /// </summary>
    /// <param name="project">Una copia que nadie más modifica mientras se escribe (por ejemplo, <see cref="Project.Clone"/>).</param>
    /// <param name="projectPath">El <c>.rodaje</c> del proyecto, o <c>null</c> si nunca se guardó.</param>
    /// <returns>La ruta de la copia.</returns>
    /// <exception cref="IOException">Si no se pudo escribir; las copias anteriores quedan como estaban.</exception>
    public string Write(Project project, string? projectPath)
    {
        ArgumentNullException.ThrowIfNull(project);
        lock (_gate)
        {
            ThrowIfCompleted();
            string path = SnapshotPath(Directory, _nextNumber);
            ProjectSerializer.Save(project, path, _fileSystem);
            _nextNumber++;
            WriteInfo(project.Name, projectPath, unsavedChanges: true);
            Rotate();
            return path;
        }
    }

    /// <summary>
    /// El proyecto está guardado (o se descartaron sus cambios a propósito): si el editor se cae ahora, no hay nada
    /// que ofrecer. Las copias se quedan como historial.
    /// </summary>
    public void MarkSaved(string projectName, string? projectPath)
    {
        ArgumentNullException.ThrowIfNull(projectName);
        lock (_gate)
        {
            ThrowIfCompleted();
            WriteInfo(projectName, projectPath, unsavedChanges: false);
        }
    }

    /// <summary>Borra las copias (se abrió otro proyecto) y deja la sesión sin nada que ofrecer.</summary>
    public void Clear(string projectName, string? projectPath)
    {
        ArgumentNullException.ThrowIfNull(projectName);
        lock (_gate)
        {
            ThrowIfCompleted();
            WriteInfo(projectName, projectPath, unsavedChanges: false);
            foreach (AutosaveSnapshot snapshot in AutosaveRecovery.ListSnapshots(Directory))
            {
                TryDelete(snapshot.Path);
            }
        }
    }

    /// <summary>Las copias de esta sesión, de la más nueva a la más vieja.</summary>
    public IReadOnlyList<AutosaveSnapshot> Snapshots
    {
        get
        {
            lock (_gate)
            {
                return _completed ? [] : AutosaveRecovery.ListSnapshots(Directory);
            }
        }
    }

    /// <summary>
    /// Cierre normal: suelta el bloqueo y borra la carpeta de la sesión. Si algo no se puede borrar, la carpeta queda
    /// sin bloqueo y no se ofrece recuperar; la limpieza de arranque la borra más adelante.
    /// </summary>
    public void Complete()
    {
        lock (_gate)
        {
            if (_completed)
            {
                return;
            }

            _completed = true;
            _lock?.Dispose();
            _lock = null;

            // El bloqueo va primero: sin él la carpeta ya no cuenta como cierre inesperado aunque quede algo.
            TryDelete(Path.Combine(Directory, LockFileName));
            try
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// Suelta el bloqueo sin borrar nada: para el siguiente arranque, esta sesión terminó mal. El cierre normal usa
    /// <see cref="Complete"/>.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _lock?.Dispose();
            _lock = null;
            _completed = true;
        }
    }

    internal static string SnapshotPath(string directory, int number) =>
        Path.Combine(directory, string.Create(CultureInfo.InvariantCulture, $"{SnapshotPrefix}{number:D6}{ProjectSerializer.FileExtension}"));

    private void ThrowIfCompleted() => ObjectDisposedException.ThrowIf(_completed, this);

    private void Rotate()
    {
        IReadOnlyList<AutosaveSnapshot> snapshots = AutosaveRecovery.ListSnapshots(Directory);
        for (int i = MaxVersions; i < snapshots.Count; i++)
        {
            TryDelete(snapshots[i].Path);
        }
    }

    private void WriteInfo(string projectName, string? projectPath, bool unsavedChanges)
    {
        var info = new JsonObject
        {
            ["projectName"] = projectName,
            ["projectPath"] = projectPath,
            ["unsavedChanges"] = unsavedChanges,
            ["updated"] = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture),
        };
        string path = Path.Combine(Directory, InfoFileName);
        string temp = path + ProjectSerializer.TempSuffix;
        byte[] bytes = Utf8NoBom.GetBytes(info.ToJsonString(Indented) + "\n");
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temp, path, overwrite: true);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Una copia de más no daña nada: la siguiente rotación lo vuelve a intentar.
        }
        catch (UnauthorizedAccessException)
        {
            // Igual que arriba.
        }
    }
}
