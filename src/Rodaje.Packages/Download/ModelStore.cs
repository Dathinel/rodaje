using System.Runtime.InteropServices;

namespace Rodaje.Packages.Download;

/// <summary>Un modelo que necesita un módulo: id para mostrar, de dónde sale (Hugging Face fijado por commit o una release), su hash y su tamaño.</summary>
public sealed record ModelSpec(string Id, Uri Url, string Sha256, long Size);

/// <summary>
/// Modelos de IA compartidos entre módulos, guardados una sola vez por contenido en <c>complementos\modelos\&lt;sha256&gt;</c>.
/// La distribución completa trae los suyos en <c>modelos\</c> junto al exe. Se bajan con <see cref="FileDownloader"/>
/// (hash al vuelo, reanudación, renombrado atómico): un archivo con el nombre de su hash está completo y verificado.
/// </summary>
public sealed partial class ModelStore(string folder, FileDownloader downloader, DownloadUrlPolicy? policy = null, IReadOnlyList<string>? bundledFolders = null)
{
    private readonly DownloadUrlPolicy _policy = policy ?? DownloadUrlPolicy.Default;

    public string Folder { get; } = Path.GetFullPath(folder ?? throw new ArgumentNullException(nameof(folder)));

    /// <summary>La ruta del modelo si ya está (en la carpeta de datos o en la de la distribución completa) con su tamaño exacto.</summary>
    public string? Find(ModelSpec model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (!SignedIndex.IsSha256(model.Sha256))
        {
            return null;
        }

        foreach (string directory in (IEnumerable<string>)[Folder, .. bundledFolders ?? []])
        {
            string path = Path.Combine(directory, model.Sha256);
            if (File.Exists(path) && new FileInfo(path).Length == model.Size)
            {
                return path;
            }
        }

        return null;
    }

    /// <summary>Devuelve la ruta del modelo, bajándolo si falta.</summary>
    /// <exception cref="PackageDownloadException">El origen no está permitido (por ejemplo, Hugging Face sin commit) o no se pudo bajar.</exception>
    /// <exception cref="DownloadVerificationException">Lo bajado no coincide con el hash; se borró.</exception>
    public async Task<string> EnsureAsync(ModelSpec model, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (Find(model) is { } existing)
        {
            progress?.Report(new DownloadProgress(model.Size, model.Size));
            return existing;
        }

        if (!SignedIndex.IsSha256(model.Sha256) || model.Size <= 0)
        {
            throw new PackageDownloadException($"El modelo «{model.Id}» no trae un SHA-256 y un tamaño válidos.");
        }

        if (!_policy.IsAllowed(model.Url, out string? reason))
        {
            throw new PackageDownloadException($"El modelo «{model.Id}» no se puede bajar: {reason}");
        }

        string path = Path.Combine(Folder, model.Sha256);
        await downloader.DownloadAsync(new DownloadTarget(model.Url, model.Sha256, model.Size), path, progress, cancellationToken).ConfigureAwait(false);
        return path;
    }

    /// <summary>
    /// Deja el modelo donde lo busca el módulo (<c>&lt;carpeta del módulo&gt;\&lt;file&gt;</c>): lo baja al almacén si falta y lo
    /// enlaza (enlace duro, sin ocupar el doble) o, si no se puede, lo copia.
    /// </summary>
    public async Task<string> PlaceAsync(ModuleModel model, string moduleDirectory, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        string target = model.PathIn(moduleDirectory);
        if (!SafePaths.IsInside(target, moduleDirectory))
        {
            throw new PackageDownloadException($"El modelo «{model.Id}» apunta fuera de la carpeta del módulo.");
        }

        if (model.IsPresentIn(moduleDirectory))
        {
            progress?.Report(new DownloadProgress(model.Size, model.Size));
            return target;
        }

        string stored = await EnsureAsync(model.Spec, progress, cancellationToken).ConfigureAwait(false);
        LinkOrCopy(stored, target);
        return target;
    }

    /// <summary>Borra del almacén los modelos que ya no usa nadie (los parciales se dejan para reanudar). Devuelve los bytes liberados.</summary>
    public long Prune(IEnumerable<string> inUse)
    {
        ArgumentNullException.ThrowIfNull(inUse);
        var keep = new HashSet<string>(inUse, StringComparer.OrdinalIgnoreCase);
        long freed = 0;
        if (!Directory.Exists(Folder))
        {
            return 0;
        }

        foreach (string path in Directory.EnumerateFiles(Folder))
        {
            string name = Path.GetFileName(path);
            if (!SignedIndex.IsSha256(name) || keep.Contains(name))
            {
                continue;
            }

            try
            {
                long length = new FileInfo(path).Length;
                File.Delete(path);
                freed += length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return freed;
    }

    /// <summary>Enlace duro de <paramref name="source"/> en <paramref name="target"/> (mismo volumen) o, si no se puede, una copia; nunca deja el destino a medias.</summary>
    public static void LinkOrCopy(string source, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (File.Exists(target))
        {
            File.Delete(target);
        }

        if (OperatingSystem.IsWindows() && CreateHardLink(target, source, 0))
        {
            return;
        }

        string temporary = target + FileDownloader.PartialSuffix;
        File.Copy(source, temporary, overwrite: true);
        File.Move(temporary, target, overwrite: true);
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateHardLinkW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateHardLink(string fileName, string existingFileName, nint securityAttributes);
}
