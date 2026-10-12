namespace Rodaje.Packages.Download;

/// <summary>Un modelo que necesita un módulo: id para mostrar, de dónde sale (Hugging Face fijado por commit o una release), su hash y su tamaño.</summary>
public sealed record ModelSpec(string Id, Uri Url, string Sha256, long Size);

/// <summary>
/// Modelos de IA compartidos entre módulos, guardados una sola vez por contenido en <c>complementos\modelos\&lt;sha256&gt;</c>.
/// La distribución completa trae los suyos en <c>modelos\</c> junto al exe. Se bajan con <see cref="FileDownloader"/>
/// (hash al vuelo, reanudación, renombrado atómico): un archivo con el nombre de su hash está completo y verificado.
/// </summary>
public sealed class ModelStore(string folder, FileDownloader downloader, DownloadUrlPolicy? policy = null, IReadOnlyList<string>? bundledFolders = null)
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
        await downloader.DownloadAsync(new DownloadTarget(model.Url, model.Sha256, model.Size), path, progress, cancellationToken);
        return path;
    }
}
