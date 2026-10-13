using System.Security.Cryptography;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Editing;

/// <summary>Un archivo de la carpeta donde se busca.</summary>
/// <param name="Path">La ruta completa.</param>
/// <param name="Length">El largo en bytes.</param>
/// <param name="CloudOnly">
/// Solo está en la nube (OneDrive y parecidos): abrirlo lo descarga entero. La pasada de renombrados no lo abre.
/// </param>
public sealed record RelinkFile(string Path, long Length, bool CloudOnly = false);

/// <summary>Lo que la búsqueda necesita del disco; las pruebas lo sustituyen por uno en memoria.</summary>
public interface IRelinkFileSystem
{
    /// <summary>Los archivos de <paramref name="folder"/> y sus subcarpetas; los que no se pueden leer se saltan.</summary>
    IEnumerable<RelinkFile> EnumerateFiles(string folder, CancellationToken cancellationToken);

    /// <summary>Los datos del archivo como medio, o <c>null</c> si no se puede usar. Puede ser lento (abre el archivo).</summary>
    RelinkProbe? Probe(string path);

    /// <summary>Una huella de los primeros <paramref name="bytes"/> bytes y del largo del archivo, o <c>null</c> si no se puede leer.</summary>
    string? Fingerprint(string path, int bytes);
}

/// <summary>El disco de verdad; leer un archivo como medio se delega (en la app lo hace FFmpeg).</summary>
public sealed class DiskRelinkFileSystem(Func<string, RelinkProbe?> probe) : IRelinkFileSystem
{
    /// <summary>
    /// Atributos de un archivo que no está en el disco: <c>Offline</c> y los de Windows que .NET no nombra,
    /// <c>FILE_ATTRIBUTE_RECALL_ON_OPEN</c> (0x40000) y <c>FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS</c> (0x400000), que pone
    /// OneDrive a lo que solo está en la nube.
    /// </summary>
    public const FileAttributes CloudOnlyAttributes = FileAttributes.Offline | (FileAttributes)0x40000 | (FileAttributes)0x400000;

    public IEnumerable<RelinkFile> EnumerateFiles(string folder, CancellationToken cancellationToken)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System };
        foreach (FileInfo file in new DirectoryInfo(folder).EnumerateFiles("*", options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new RelinkFile(file.FullName, file.Length, (file.Attributes & CloudOnlyAttributes) != 0);
        }
    }

    public RelinkProbe? Probe(string path) => probe(path);

    public string? Fingerprint(string path, int bytes)
    {
        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            byte[] buffer = new byte[(int)Math.Min(bytes, stream.Length)];
            stream.ReadExactly(buffer);
            return $"{stream.Length}:{Convert.ToHexString(SHA256.HashData(buffer))}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>Cómo se encontró un archivo.</summary>
public enum RelinkMatchKind
{
    /// <summary>Con el mismo nombre y los mismos datos.</summary>
    SameName,

    /// <summary>Con otro nombre (renombrado), pero la misma extensión y los mismos datos.</summary>
    Renamed,
}

/// <summary>El archivo que se encontró para un medio que faltaba.</summary>
public sealed record RelinkMatch(string MediaId, string Path, RelinkMatchKind Kind)
{
    /// <summary>
    /// Hay que confirmarlo antes de enlazar: con otro nombre solo se comparan la extensión, las pistas, el tamaño de imagen y
    /// la duración, no el contenido (el proyecto no guarda una huella del original), así que puede ser otro archivo parecido.
    /// </summary>
    public bool NeedsConfirmation => Kind == RelinkMatchKind.Renamed;
}

/// <summary>Lo que dio la búsqueda en una carpeta.</summary>
/// <param name="Found">Los medios con un solo archivo posible (o varios idénticos), en el orden de la lista pedida.</param>
/// <param name="Ambiguous">Los medios con varios archivos distintos que encajan igual de bien: se eligen a mano.</param>
/// <param name="NotFound">Los medios sin ningún archivo que encaje.</param>
/// <param name="FilesScanned">Archivos recorridos.</param>
/// <param name="Truncated">La carpeta tenía más de <see cref="RelinkSearchOptions.MaxFiles"/> archivos y no se vio entera.</param>
public sealed record RelinkSearchResult(
    IReadOnlyList<RelinkMatch> Found,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Ambiguous,
    IReadOnlyList<string> NotFound,
    int FilesScanned,
    bool Truncated)
{
    /// <summary>
    /// Lo encontrado, listo para <see cref="Commands.RelinkMediaCommand"/>; incluye lo que pide confirmación
    /// (<see cref="RelinkMatch.NeedsConfirmation"/>), que el diálogo no aplica sin que el usuario lo confirme.
    /// </summary>
    public IReadOnlyList<MediaRelinkTarget> Targets => [.. Found.Select(f => new MediaRelinkTarget(f.MediaId, f.Path))];
}

/// <summary>Límites de la búsqueda.</summary>
public sealed record RelinkSearchOptions
{
    /// <summary>Margen de duración para dar un archivo por el mismo medio.</summary>
    public Time DurationTolerance { get; init; } = MediaRelinkEdits.DefaultDurationTolerance;

    /// <summary>Bytes del principio del archivo que se comparan cuando hay varios candidatos.</summary>
    public int FingerprintBytes { get; init; } = 4 << 20;

    /// <summary>Archivos que se recorren como mucho (una carpeta enorme, como la raíz de un disco, no se recorre entera).</summary>
    public int MaxFiles { get; init; } = 100_000;

    /// <summary>Busca también archivos renombrados (otro nombre, misma extensión y mismos datos); hay que leerlos todos.</summary>
    public bool IncludeRenamed { get; init; } = true;

    /// <summary>
    /// Rutas que no se dan a ningún medio (sin distinguir mayúsculas), además de las de los medios que no faltan: las que el
    /// diálogo ya resolvió para otros medios en una búsqueda anterior o a mano.
    /// </summary>
    public IReadOnlyCollection<string> TakenPaths { get; init; } = [];
}

/// <summary>
/// Busca en una carpeta los archivos de los medios que faltan. Primero por nombre: un archivo con el mismo nombre (sin
/// importar mayúsculas) que tenga las mismas pistas, el mismo tamaño de imagen y la misma duración. Después, para los que
/// sigan sin archivo, uno renombrado: misma extensión y mismos datos. Si encajan varios, gana el que conserva más carpetas
/// de la ruta de antes (<c>material\día 1\toma.mp4</c>); si siguen empatados se comparan sus huellas (los primeros MB y
/// el largo) y, si son copias idénticas, se toma cualquiera; si no, se deja elegir a mano. Un archivo no se da a dos medios
/// ni a uno que ya tiene el suyo.
/// </summary>
public static class MediaRelinkSearch
{
    public static RelinkSearchResult Search(
        Project project, IReadOnlyList<MediaRef> missing, string folder, IRelinkFileSystem fileSystem,
        RelinkSearchOptions? options = null, IProgress<int>? filesScanned = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(missing);
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(fileSystem);
        options ??= new RelinkSearchOptions();

        var files = new List<RelinkFile>();
        bool truncated = false;
        foreach (RelinkFile file in fileSystem.EnumerateFiles(folder, cancellationToken))
        {
            if (files.Count >= options.MaxFiles)
            {
                truncated = true;
                break;
            }

            files.Add(file);
            if (files.Count % 500 == 0)
            {
                filesScanned?.Report(files.Count);
            }
        }

        filesScanned?.Report(files.Count);
        files.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        ILookup<string, RelinkFile> byName = files.ToLookup(f => FileName(f.Path), StringComparer.OrdinalIgnoreCase);

        var missingIds = missing.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        // Las rutas ocupadas se guardan normalizadas: una escrita con «.» o «..» es el mismo archivo.
        var taken = new HashSet<string>(
            project.Media.Where(m => !missingIds.Contains(m.Id)).Select(m => m.Path).Concat(options.TakenPaths)
                .Select(MediaRelinkEdits.NormalizePath),
            StringComparer.OrdinalIgnoreCase);
        bool IsTaken(string path) => taken.Contains(MediaRelinkEdits.NormalizePath(path));
        var probes = new Dictionary<string, RelinkProbe?>(StringComparer.Ordinal);
        var fingerprints = new Dictionary<string, string?>(StringComparer.Ordinal);
        var found = new Dictionary<string, RelinkMatch>(StringComparer.Ordinal);
        var ambiguous = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        RelinkProbe? ProbeOf(string path)
        {
            if (!probes.TryGetValue(path, out RelinkProbe? probe))
            {
                cancellationToken.ThrowIfCancellationRequested();
                probe = fileSystem.Probe(path);
                probes[path] = probe;
            }

            return probe;
        }

        string? FingerprintOf(string path)
        {
            if (!fingerprints.TryGetValue(path, out string? print))
            {
                cancellationToken.ThrowIfCancellationRequested();
                print = fileSystem.Fingerprint(path, options.FingerprintBytes);
                fingerprints[path] = print;
            }

            return print;
        }

        void Pass(RelinkMatchKind kind, Func<MediaRef, IEnumerable<RelinkFile>> candidatesOf)
        {
            foreach (MediaRef media in missing)
            {
                if (found.ContainsKey(media.Id) || ambiguous.ContainsKey(media.Id))
                {
                    continue;
                }

                RelinkProbe expected = RelinkProbe.Of(media);
                List<string> fitting = [.. candidatesOf(media)
                    .Select(f => f.Path)
                    .Where(p => !IsTaken(p) && ProbeOf(p) is { } probe && probe.Matches(expected, options.DurationTolerance))];
                if (Choose(media.Path, fitting, FingerprintOf, out List<string> tied) is { } chosen)
                {
                    found[media.Id] = new RelinkMatch(media.Id, chosen, kind);
                    taken.Add(MediaRelinkEdits.NormalizePath(chosen));
                }
                else if (tied.Count > 1)
                {
                    ambiguous[media.Id] = tied;
                }
            }
        }

        Pass(RelinkMatchKind.SameName, m => byName[FileName(m.Path)]);
        if (options.IncludeRenamed)
        {
            Pass(RelinkMatchKind.Renamed, m =>
            {
                string name = FileName(m.Path);
                string extension = Extension(name);
                // Esta pasada abre cada archivo con la misma extensión: los que solo están en la nube no se abren, porque
                // abrirlos los descargaría. Uno con el mismo nombre sí se abre en la pasada anterior: es uno solo y casi
                // seguro el que se busca.
                return files.Where(f =>
                    !f.CloudOnly
                    && !string.Equals(FileName(f.Path), name, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(Extension(FileName(f.Path)), extension, StringComparison.OrdinalIgnoreCase));
            });
        }

        // Un archivo que quedó dudoso para un medio pudo dárselo después a otro: se quita de sus opciones.
        foreach (string id in ambiguous.Keys.ToList())
        {
            List<string> left = [.. ambiguous[id].Where(p => !IsTaken(p))];
            if (left.Count == 0)
            {
                ambiguous.Remove(id);
            }
            else
            {
                ambiguous[id] = left;
            }
        }

        return new RelinkSearchResult(
            [.. missing.Where(m => found.ContainsKey(m.Id)).Select(m => found[m.Id])],
            ambiguous,
            [.. missing.Where(m => !found.ContainsKey(m.Id) && !ambiguous.ContainsKey(m.Id)).Select(m => m.Id)],
            files.Count,
            truncated);
    }

    /// <summary>
    /// El nombre del archivo de una ruta con <c>\</c> o <c>/</c>: las rutas del proyecto pueden venir de Windows aunque se
    /// lean en otro sistema.
    /// </summary>
    public static string FileName(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        int slash = path.AsSpan().LastIndexOfAny('\\', '/');
        return path[(slash + 1)..];
    }

    private static string Extension(string fileName)
    {
        int dot = fileName.LastIndexOf('.');
        return dot <= 0 ? "" : fileName[dot..];
    }

    private static string? Choose(string originalPath, List<string> fitting, Func<string, string?> fingerprintOf, out List<string> top)
    {
        top = fitting;
        if (fitting.Count <= 1)
        {
            return fitting.Count == 1 ? fitting[0] : null;
        }

        int best = fitting.Max(p => SharedTail(originalPath, p));
        List<string> tied = [.. fitting.Where(p => SharedTail(originalPath, p) == best)];
        top = tied;
        if (tied.Count == 1)
        {
            return tied[0];
        }

        string? first = fingerprintOf(tied[0]);
        return first is not null && tied.Skip(1).All(p => fingerprintOf(p) == first) ? tied[0] : null;
    }

    /// <summary>Cuántas carpetas, contando desde el archivo hacia arriba, tienen el mismo nombre en las dos rutas.</summary>
    private static int SharedTail(string a, string b)
    {
        string[] left = a.Split('\\', '/');
        string[] right = b.Split('\\', '/');
        int shared = 0;
        for (int i = 2; i <= Math.Min(left.Length, right.Length); i++)
        {
            if (!string.Equals(left[^i], right[^i], StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            shared++;
        }

        return shared;
    }
}
