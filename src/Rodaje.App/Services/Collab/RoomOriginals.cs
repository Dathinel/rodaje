using Rodaje.App.Services.Operations;
using Rodaje.App.Services.Packages;

namespace Rodaje.App.Services.Collab;

/// <summary>Cómo acabó un original de «Pedir todos los originales»: la ruta si llegó, o el porqué si no.</summary>
public sealed record OriginalResult(string MediaId, string Name, string? Path, string? Problem);

/// <summary>
/// Los originales que el invitado pide a quien hospeda. Con el manifiesto de medios que llegó con el proyecto (id → tamaño y
/// SHA-256 entero) sabe qué se puede pedir y cuánto pesa; los pide de uno en uno (los demás esperan su turno) y los deja en
/// <c>medios\</c> dentro de la carpeta de la sala. La ruta que devuelve es absoluta: es la que se enlaza al medio.
/// </summary>
public sealed class RoomOriginals
{
    /// <summary>La subcarpeta de la sala donde se guardan los originales recibidos.</summary>
    public const string FolderName = "medios";

    private readonly RoomService? _room;
    private readonly Func<long?> _maxBytes;
    private readonly OriginalGuestOptions? _options;
    private readonly Dictionary<string, MediaManifestEntry> _entries = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _one = new(1, 1);
    private int _pending;

    /// <param name="room">La sala por la que pedir; nula solo en pruebas (entonces pedir lanza).</param>
    /// <param name="media">El manifiesto de medios del anfitrión, recibido junto al proyecto.</param>
    /// <param name="maxBytes">El tope de Preferencias › Colaboración; <c>null</c>, sin tope.</param>
    public RoomOriginals(RoomService? room, string roomFolder, IEnumerable<MediaManifestEntry> media, Func<long?>? maxBytes = null, OriginalGuestOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(roomFolder);
        ArgumentNullException.ThrowIfNull(media);
        _room = room;
        MediaFolder = Path.Combine(Path.GetFullPath(roomFolder), FolderName);
        _maxBytes = maxBytes ?? (() => null);
        _options = options;
        foreach (MediaManifestEntry entry in media)
        {
            _entries.TryAdd(entry.MediaId, entry);
        }
    }

    /// <summary>Dónde quedan los originales recibidos.</summary>
    public string MediaFolder { get; }

    /// <summary>Originales pedidos que aún no terminaron (el que llega y los que esperan).</summary>
    public int Pending => Volatile.Read(ref _pending);

    /// <summary>Si este medio del proyecto de la sala se puede pedir (está en el manifiesto de quien hospeda).</summary>
    public bool CanRequest(string mediaId) => _entries.ContainsKey(mediaId);

    /// <summary>El tamaño del original, para enseñarlo antes de pedirlo; <c>null</c> si no está en el manifiesto.</summary>
    public long? SizeOf(string mediaId) => _entries.TryGetValue(mediaId, out MediaManifestEntry? entry) ? entry.Size : null;

    /// <summary>«Pedir el original a quien hospeda (1,2 GB)».</summary>
    public string? MenuText(string mediaId) =>
        SizeOf(mediaId) is { } size ? $"Pedir el original a quien hospeda ({PackageService.FormatSize(size)})" : null;

    /// <summary>
    /// Pide el original del medio a quien hospeda, lo comprueba y lo deja en <see cref="MediaFolder"/>. Devuelve la ruta
    /// absoluta, que es la que hay que enlazar al medio (con <c>RelinkMediaCommand</c>). Uno a la vez: si hay otro en
    /// curso, este espera su turno.
    /// </summary>
    /// <exception cref="CollabException">No se puede, no cabe, se negó, no llegó o llegó distinto; el texto dice qué hacer.</exception>
    public async Task<string> RequestOriginalAsync(string mediaId, OperationProgress? progress = null, CancellationToken cancellationToken = default)
    {
        string path = await RequestCoreAsync(Entry(mediaId), progress, null, cancellationToken).ConfigureAwait(false);
        progress?.Complete();
        return path;
    }

    /// <summary>
    /// Pide todos estos originales en cola, de uno en uno («Original 2 de 5»). Lo que falla no para a los demás; cancelar
    /// para la cola y devuelve cómo acabó cada uno de los intentados (con su ruta los que llegaron), en el orden pedido.
    /// </summary>
    public async Task<IReadOnlyList<OriginalResult>> RequestAllAsync(IReadOnlyList<string> mediaIds, OperationProgress? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mediaIds);
        List<MediaManifestEntry> wanted = [.. mediaIds.Distinct(StringComparer.Ordinal).Select(Entry)];
        List<OriginalResult> results = [];
        for (int i = 0; i < wanted.Count; i++)
        {
            MediaManifestEntry entry = wanted[i];
            try
            {
                string path = await RequestCoreAsync(entry, progress, $"Original {i + 1} de {wanted.Count}", cancellationToken).ConfigureAwait(false);
                results.Add(new OriginalResult(entry.MediaId, entry.Name, path, null));
            }
            catch (CollabException ex)
            {
                results.Add(new OriginalResult(entry.MediaId, entry.Name, null, ex.Message));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }

        progress?.Complete();
        return results;
    }

    private MediaManifestEntry Entry(string mediaId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaId);
        return _entries.TryGetValue(mediaId, out MediaManifestEntry? entry)
            ? entry
            : throw new CollabException("Ese medio no se puede pedir así. Quien hospeda no lo dio a conocer al abrir la sala.");
    }

    private async Task<string> RequestCoreAsync(MediaManifestEntry entry, OperationProgress? progress, string? label, CancellationToken cancellationToken)
    {
        if (_room is not { } room)
        {
            throw new InvalidOperationException("Se pidió un original sin estar en una sala.");
        }

        if (_maxBytes() is { } cap && entry.Size > cap)
        {
            throw new CollabException($"«{entry.Name}» pesa {PackageService.FormatSize(entry.Size)} y pasa del tope de {PackageService.FormatSize(cap)} que pusiste en Preferencias › Colaboración. Sube el tope o quítalo y vuelve a pedirlo.");
        }

        Interlocked.Increment(ref _pending);
        try
        {
            progress?.SetCancellable(true);
            if (_one.CurrentCount == 0)
            {
                progress?.SetWaiting(true);
                progress?.SetPhase(1, OriginalShareGuest.Steps, Prefixed(label, "Esperando a que terminen los otros originales…"));
            }

            await _one.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                progress?.SetWaiting(false);
                return await OriginalShareGuest.RequestAsync(room, entry, MediaFolder, Adapt(progress, label), _options, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _one.Release();
            }
        }
        finally
        {
            Interlocked.Decrement(ref _pending);
        }
    }

    private static string Prefixed(string? label, string text) => label is null ? text : $"{label}: {text}";

    private static IProgress<ShareProgress>? Adapt(OperationProgress? operation, string? label) =>
        operation is null ? null : new ToOperation(operation, label);

    private sealed class ToOperation(OperationProgress operation, string? label) : IProgress<ShareProgress>
    {
        public void Report(ShareProgress value)
        {
            operation.SetPhase(value.Step, value.Steps, Prefixed(label, value.Text));
            operation.Report(value.Fraction);
        }
    }
}
