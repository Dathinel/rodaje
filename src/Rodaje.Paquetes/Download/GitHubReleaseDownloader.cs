using System.Globalization;
using System.Security.Cryptography;

namespace Rodaje.Paquetes.Download;

/// <summary>Configuración de <see cref="GitHubReleaseDownloader"/>.</summary>
public sealed record GitHubReleaseOptions
{
    /// <summary>
    /// <c>indice.json</c> de la última release del catálogo. <c>releases/latest/download</c> redirige al asset sin pasar por
    /// la API de GitHub (sin la cuota de 60 consultas por hora).
    /// </summary>
    public required Uri IndexUrl { get; init; }

    /// <summary>La firma; por defecto, la misma dirección con <c>.sig</c>.</summary>
    public Uri? SignatureUrl { get; init; }

    /// <summary>Clave pública P-256 (SubjectPublicKeyInfo en base64). Sin ella no se descarga nada.</summary>
    public required string? PublicKey { get; init; }

    /// <summary>Carpeta de descargas (los parciales quedan ahí para reanudar).</summary>
    public required string DownloadFolder { get; init; }

    /// <summary>Versión del programa, para elegir la versión de cada paquete según su <c>minApp</c>.</summary>
    public required PackageVersion AppVersion { get; init; }

    public DownloadUrlPolicy Policy { get; init; } = DownloadUrlPolicy.Default;

    /// <summary>Cuánto se reutiliza un índice ya leído antes de volver a pedirlo.</summary>
    public TimeSpan IndexMaxAge { get; init; } = TimeSpan.FromMinutes(10);
}

/// <summary>
/// Descarga complementos desde las releases de GitHub con un índice firmado. El índice (<c>indice.json</c> +
/// <c>indice.json.sig</c>) se comprueba con la clave pública del programa antes de leerlo, y no se acepta uno más viejo
/// que el último visto (así nadie puede servir un índice anterior con versiones vulnerables). Cada archivo se baja con
/// <see cref="FileDownloader"/>: hash al vuelo, reanudación y borrado si no coincide.
/// </summary>
public sealed class GitHubReleaseDownloader : IPackageDownloader, IDisposable
{
    private const string StateFileName = "indice-visto.txt";

    private readonly GitHubReleaseOptions _options;
    private readonly ECDsa? _key;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _indexGate = new(1, 1);
    private SignedIndex? _index;
    private long _indexTime;

    public GitHubReleaseDownloader(GitHubReleaseOptions options, HttpClient http)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _key = IndexSignature.TryImportPublicKey(options.PublicKey);
        Files = new FileDownloader(http);
    }

    public FileDownloader Files { get; init; }

    public bool IsAvailable => _key is not null;

    public string? UnavailableReason => _key is null ? "Esta versión de Rodaje no trae la clave del catálogo: instala desde archivo." : null;

    /// <summary>Lo que se aprendió al leer el último índice (entradas omitidas), para registrarlo.</summary>
    public IReadOnlyList<string> IndexProblems { get; private set; } = [];

    /// <summary>El índice firmado, del caché si es reciente.</summary>
    /// <exception cref="DownloadVerificationException">La firma no es válida o el índice es más viejo que el último visto.</exception>
    /// <exception cref="PackageDownloadException">No se pudo bajar.</exception>
    public async Task<SignedIndex> GetIndexAsync(bool refresh = false, CancellationToken cancellationToken = default)
    {
        if (_key is null)
        {
            throw new PackageDownloadException(UnavailableReason!);
        }

        await _indexGate.WaitAsync(cancellationToken);
        try
        {
            if (!refresh && _index is not null && Environment.TickCount64 - _indexTime < _options.IndexMaxAge.TotalMilliseconds)
            {
                return _index;
            }

            Uri signatureUrl = _options.SignatureUrl ?? new Uri(_options.IndexUrl.AbsoluteUri + ".sig");
            byte[] json = await FetchSmallAsync(_options.IndexUrl, SignedIndex.MaxBytes, cancellationToken);
            byte[] signature = await FetchSmallAsync(signatureUrl, IndexSignature.MaxSignatureBytes, cancellationToken);
            var problems = new List<string>();
            SignedIndex index;
            try
            {
                index = SignedIndex.Load(json, signature, _key, _options.Policy, problems);
            }
            catch (FormatException ex)
            {
                throw new PackageDownloadException(ex.Message, ex);
            }

            DateTimeOffset? seen = LastSeen();
            if (seen is { } last && index.Generated < last)
            {
                throw new DownloadVerificationException("El índice de complementos es más viejo que uno ya visto; no se usó.");
            }

            RememberSeen(index.Generated);
            IndexProblems = problems;
            _index = index;
            _indexTime = Environment.TickCount64;
            return index;
        }
        finally
        {
            _indexGate.Release();
        }
    }

    /// <summary>La versión más nueva publicada de cada paquete o módulo que funciona con este programa.</summary>
    public async Task<IReadOnlyDictionary<string, PackageVersion>> AvailableVersionsAsync(CancellationToken cancellationToken)
    {
        SignedIndex index = await GetIndexAsync(cancellationToken: cancellationToken);
        return index.Entries.Where(e => e.Kind != IndexEntryKind.Model && e.WorksWith(_options.AppVersion))
            .GroupBy(e => e.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Max(e => e.Version)!, StringComparer.Ordinal);
    }

    public async Task<DownloadedPackage> DownloadAsync(CatalogEntry entry, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        SignedIndex index = await GetIndexAsync(cancellationToken: cancellationToken);
        IndexEntryKind kind = entry.Kind == PackageKind.Module ? IndexEntryKind.Module : IndexEntryKind.Content;
        IndexEntry? found = index.Latest(entry.Id, _options.AppVersion, kind);
        if (found is null)
        {
            throw new PackageDownloadException(index.Newest(entry.Id) is { MinApp: { } min }
                ? $"«{entry.Name}» necesita Rodaje {min} o posterior."
                : $"«{entry.Name}» todavía no está publicado.");
        }

        string path = Path.Combine(_options.DownloadFolder, found.Sha256 + PackageManifest.FileExtension);
        IProgress<DownloadProgress>? bytes = progress is null ? null : new SyncProgress(p => progress.Report(p.Fraction));
        await Files.DownloadAsync(new DownloadTarget(found.Url, found.Sha256, found.Size), path, bytes, cancellationToken);
        return new DownloadedPackage(path, found.Sha256);
    }

    private async Task<byte[]> FetchSmallAsync(Uri url, int maxBytes, CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new PackageDownloadException($"No se pudo leer el catálogo de complementos ({(int)response.StatusCode} {response.ReasonPhrase}).");
            }

            if (response.Content.Headers.ContentLength > maxBytes)
            {
                throw new DownloadVerificationException("El índice de complementos es demasiado grande; no se usó.");
            }

            await using Stream body = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var buffer = new MemoryStream();
            byte[] chunk = new byte[16384];
            int read;
            while ((read = await body.ReadAsync(chunk, cancellationToken)) > 0)
            {
                if (buffer.Length + read > maxBytes)
                {
                    throw new DownloadVerificationException("El índice de complementos es demasiado grande; no se usó.");
                }

                buffer.Write(chunk, 0, read);
            }

            return buffer.ToArray();
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new PackageDownloadException("No hay conexión con GitHub: " + ex.Message, ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PackageDownloadException("GitHub no respondió a tiempo.", ex);
        }
    }

    private string StatePath => Path.Combine(_options.DownloadFolder, StateFileName);

    private DateTimeOffset? LastSeen()
    {
        try
        {
            return File.Exists(StatePath) && DateTimeOffset.TryParse(File.ReadAllText(StatePath).Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset seen)
                ? seen
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void RememberSeen(DateTimeOffset generated)
    {
        try
        {
            Directory.CreateDirectory(_options.DownloadFolder);
            string temporary = StatePath + ".tmp";
            File.WriteAllText(temporary, generated.ToString("O", CultureInfo.InvariantCulture));
            File.Move(temporary, StatePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public void Dispose()
    {
        _key?.Dispose();
        _indexGate.Dispose();
    }

    /// <summary>Progreso sin pasar por el contexto de sincronización (lo reenvía quien llama).</summary>
    private sealed class SyncProgress(Action<DownloadProgress> report) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => report(value);
    }
}
