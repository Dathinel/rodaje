using Rodaje.App.Services.Operations;

namespace Rodaje.App.Services.Collab;

/// <summary>
/// Las copias ligeras de la sala en el equipo del invitado. Con el manifiesto de medios que llegó con el proyecto
/// (id → SHA-256 entero) sabe qué medios se pueden pedir a quien hospeda, y nombra el archivo que baja
/// (<c>proxies\proxy-&lt;huella&gt;.mp4</c> dentro de la carpeta de la sala): la ruta del medio apunta ahí mientras el
/// original falte, así el reproductor lo abre como a cualquier otro archivo, con su video y su audio.
/// </summary>
public sealed class RoomProxies
{
    /// <summary>La subcarpeta de la sala donde viven las copias ligeras.</summary>
    public const string FolderName = "proxies";

    /// <summary>El prefijo que distingue una copia ligera de la sala de un archivo cualquiera del usuario.</summary>
    public const string FilePrefix = "proxy-";

    private readonly RoomService? _room;
    private readonly string _roomFolder;
    private readonly Dictionary<string, MediaManifestEntry> _entries = new(StringComparer.Ordinal);

    /// <param name="room">La sala por la que pedir; nula solo en pruebas (entonces <see cref="RequestAsync"/> lanza).</param>
    /// <param name="media">El manifiesto de medios del anfitrión, recibido junto al proyecto.</param>
    public RoomProxies(RoomService? room, string roomFolder, IEnumerable<MediaManifestEntry> media)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(roomFolder);
        ArgumentNullException.ThrowIfNull(media);
        _room = room;
        _roomFolder = Path.GetFullPath(roomFolder);
        foreach (MediaManifestEntry entry in media)
        {
            _entries.TryAdd(entry.MediaId, entry);
        }
    }

    /// <summary>Si este medio del proyecto de la sala se puede pedir como copia ligera (está en el manifiesto).</summary>
    public bool CanRequest(string mediaId) => _entries.ContainsKey(mediaId);

    /// <summary>
    /// La ruta relativa (a la carpeta de la sala) que se guarda en <see cref="Core.Model.MediaRef.Path"/> al descargar la
    /// copia ligera; <c>null</c> si el medio no está en el manifiesto.
    /// </summary>
    public string? RelativePathFor(string mediaId)
    {
        return _entries.TryGetValue(mediaId, out MediaManifestEntry? entry)
            ? FolderName + Path.DirectorySeparatorChar + FileName(entry.Hash)
            : null;
    }

    /// <summary>La ruta absoluta de la copia ligera de este medio, exista o no el archivo todavía.</summary>
    public string? AbsolutePathFor(string mediaId)
    {
        return _entries.TryGetValue(mediaId, out MediaManifestEntry? entry)
            ? Path.Combine(_roomFolder, FolderName, FileName(entry.Hash))
            : null;
    }

    /// <summary>Si ya se descargó la copia ligera de este medio.</summary>
    public bool Has(string mediaId)
    {
        string? path = AbsolutePathFor(mediaId);
        return path is not null && File.Exists(path);
    }

    /// <summary>
    /// Pide la copia ligera del medio a quien hospeda y la deja en la carpeta de la sala. Devuelve la ruta relativa que
    /// debe quedar en el medio. El invitado no manda rutas: solo la huella del archivo que falta.
    /// </summary>
    /// <exception cref="CollabException">No llegó o llegó dañada; el texto dice qué hacer después.</exception>
    public async Task<string> RequestAsync(string mediaId, OperationProgress? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaId);
        if (!_entries.TryGetValue(mediaId, out MediaManifestEntry? entry))
        {
            throw new CollabException("Ese medio no se puede pedir así. Quien hospeda no lo dio a conocer al abrir la sala.");
        }

        if (_room is not { } room)
        {
            throw new InvalidOperationException("Se pidió una copia ligera sin estar en una sala.");
        }

        string destination = Path.Combine(_roomFolder, FolderName, FileName(entry.Hash));
        progress?.SetCancellable(true);
        await ProxyShareGuest.RequestAsync(room, entry.Hash, destination, Adapt(progress), cancellationToken).ConfigureAwait(false);
        progress?.Complete();
        return RelativePathFor(mediaId)
            ?? throw new InvalidOperationException("El medio dejó de estar en el manifiesto durante la descarga.");
    }

    /// <summary>El nombre de archivo de la copia ligera de una huella.</summary>
    public static string FileName(string hash) => FilePrefix + hash + ".mp4";

    /// <summary>
    /// Si <paramref name="resolvedPath"/> apunta a una copia ligera de la sala (un archivo <c>proxy-&lt;huella&gt;.mp4</c>
    /// dentro de una carpeta <c>proxies</c>), la marca que distingue un medio «solo proxy» del original.
    /// </summary>
    public static bool IsRoomProxyPath(string resolvedPath)
    {
        ArgumentNullException.ThrowIfNull(resolvedPath);
        string name = Path.GetFileName(resolvedPath);
        if (name.Length != FilePrefix.Length + 64 + 4
            || !name.StartsWith(FilePrefix, StringComparison.Ordinal)
            || !name.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
            || !MediaManifest.IsHash(name[FilePrefix.Length..^4]))
        {
            return false;
        }

        string? parent = Path.GetFileName(Path.GetDirectoryName(resolvedPath));
        return string.Equals(parent, FolderName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Los pasos del reparto (preparando en el anfitrión, recibiendo) como fases de una <see cref="OperationProgress"/>.</summary>
    public static IProgress<ShareProgress>? Adapt(OperationProgress? operation)
        => operation is null ? null : new ToOperation(operation);

    private sealed class ToOperation(OperationProgress operation) : IProgress<ShareProgress>
    {
        public void Report(ShareProgress value)
        {
            operation.SetPhase(value.Step, value.Steps, value.Text);
            operation.Report(value.Fraction);
        }
    }
}
