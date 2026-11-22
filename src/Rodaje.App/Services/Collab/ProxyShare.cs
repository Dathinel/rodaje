using System.Security.Cryptography;
using System.Text.Json;
using Dathinel.Sala;
using Rodaje.App.Services.Crashes;

namespace Rodaje.App.Services.Collab;

/// <summary>
/// Quien hospeda la sala hace el proxy de sala de un medio (video bajo con audio) y lo deja dentro de la carpeta de la sala. La
/// implementación real usa <c>ProxyGenerator</c>; aquí es una costura para poder probar el reparto sin FFmpeg.
/// </summary>
public interface IRoomProxyMaker
{
    /// <summary>Busca el medio por el SHA-256 de su archivo entero y hace su proxy; <c>null</c> si este equipo no lo tiene.</summary>
    /// <param name="progress">De 0 a 1 mientras codifica.</param>
    Task<RoomProxy?> MakeAsync(string mediaHash, string folder, IProgress<double>? progress, CancellationToken cancellationToken);
}

/// <summary>Un proxy de sala listo: el archivo (dentro de la carpeta de la sala), su SHA-256 y su tamaño.</summary>
public sealed record RoomProxy(string Path, string Sha256, long Size);

/// <summary>Los mensajes directos del reparto de proxies (fuera del orden de la sala).</summary>
internal static class ProxyMessages
{
    public const string Ask = "pedir-proxy";
    public const string Ready = "proxy";
    public const string Failed = "proxy-error";
    public const string Preparing = "proxy-preparando";
    public const long MaxProxyBytes = 8L << 30;
}

/// <summary>
/// El lado del anfitrión del «Pedir proxy»: un invitado sin el original pide el proxy de un medio por el SHA-256 de su archivo; se
/// hace una vez por medio (se guarda por huella) y se ofrece como blob. Uno cada vez; los demás esperan su turno. Solo se atiende a
/// quien está en la sala con permiso de editar o mirar, nunca una ruta: el medio se busca por huella.
/// </summary>
public sealed partial class ProxyShareHost : IDisposable
{
    private readonly RoomService _room;
    private readonly IRoomProxyMaker _maker;
    private readonly string _folder;
    private readonly Func<CancellationToken, Task<Diagnostico?>> _diagnostic;
    private readonly SemaphoreSlim _one = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    // Leído una vez: los avisos sueltos pueden llegar después de Dispose, cuando _stop ya no da su token.
    private readonly CancellationToken _token;
    private readonly Dictionary<string, RoomProxy> _made = new(StringComparer.Ordinal);

    /// <param name="diagnostic">Cómo leer el diagnóstico de la sala (el de <see cref="RoomService"/> salvo en las pruebas).</param>
    public ProxyShareHost(RoomService room, IRoomProxyMaker maker, string roomFolder,
        Func<CancellationToken, Task<Diagnostico?>>? diagnostic = null)
    {
        _room = room;
        _maker = maker;
        _folder = Path.Combine(Path.GetFullPath(roomFolder), "proxies");
        _diagnostic = diagnostic ?? room.DiagnosticAsync;
        _token = _stop.Token;
        room.DirectReceived += OnDirect;
    }

    public void Dispose()
    {
        _room.DirectReceived -= OnDirect;
        _stop.Cancel();
        _stop.Dispose();
    }

    private void OnDirect(string from, JsonElement message)
    {
        if (ShareMessages.Type(message) == ProxyMessages.Ask && _room.State is { IsHost: true, Phase: RoomPhase.Hosting }
            && message.TryGetProperty("sha256", out JsonElement h) && h.ValueKind == JsonValueKind.String && h.GetString() is { } hash && MediaManifest.IsHash(hash))
        {
            UiTask.Run(Task.Run(() => ServeAsync(from, hash)));
        }
    }

    /// <summary>Lo que oye el invitado cuando la copia ligera solo podría viajar por el relé público.</summary>
    public const string RelayRefusal = "Por internet con el relé de pruebas no se mandan proxies.";

    /// <summary>
    /// Si el camino hasta <paramref name="peer"/> pasa por el relé público de n0: los proxies no van por ahí (son
    /// archivos grandes y el relé es de pruebas). Solo se mandan por conexión directa o por un relé propio; un relé
    /// propio se conoce por <see cref="DiagnosticoRed.ReleSolicitado"/> que ya no es «n0».
    /// </summary>
    public static bool OverPublicRelay(Diagnostico? diagnostic, string peer)
    {
        if (diagnostic is null || diagnostic.Red.ReleSolicitado != "n0")
        {
            return false;
        }

        if (diagnostic.Red.SoloRele)
        {
            return true;
        }

        return diagnostic.Pares.FirstOrDefault(p => p.Clave == peer)?.Camino == Camino.Rele;
    }

    private async Task ServeAsync(string to, string mediaHash)
    {
        try
        {
            if (OverPublicRelay(await DirectShare.ReadDiagnosticAsync(_diagnostic, _stop.Token).ConfigureAwait(false), to))
            {
                await Fail(to, mediaHash, RelayRefusal).ConfigureAwait(false);
                return;
            }

            await _one.WaitAsync(_stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            RoomProxy? proxy;
            lock (_made)
            {
                _made.TryGetValue(mediaHash, out proxy);
            }

            if (proxy is null || !File.Exists(proxy.Path))
            {
                Directory.CreateDirectory(_folder);
                try
                {
                    proxy = await _maker.MakeAsync(mediaHash, _folder, new Throttled(this, to, mediaHash), _stop.Token).ConfigureAwait(false);
                }
                catch (CollabException ex)
                {
                    // El maker es código de este equipo: su texto, en español y sin rutas, es lo que hay que explicar.
                    await Fail(to, mediaHash, ex.Message).ConfigureAwait(false);
                    return;
                }

                if (proxy is null)
                {
                    await Fail(to, mediaHash, "Quien hospeda la sala no tiene ese archivo, así que no puede hacer su copia ligera.").ConfigureAwait(false);
                    return;
                }

                if (!DirectShare.IsInside(_folder, proxy.Path) || proxy.Size is <= 0 or > ProxyMessages.MaxProxyBytes || !MediaManifest.IsHash(proxy.Sha256))
                {
                    await Fail(to, mediaHash, "No se pudo preparar la copia ligera.").ConfigureAwait(false);
                    return;
                }

                lock (_made)
                {
                    _made[mediaHash] = proxy;
                }
            }

            await _room.OfferBlobAsync(proxy.Path, proxy.Sha256, proxy.Size, _stop.Token).ConfigureAwait(false);
            await _room.SendDirectAsync(ShareMessages.Make(ProxyMessages.Ready, w =>
            {
                w.WriteString("sha256", mediaHash);
                w.WriteString("huella", proxy.Sha256);
                w.WriteNumber("tam", proxy.Size);
            }), to, _stop.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (ex is not OperationCanceledException)
            {
                await Fail(to, mediaHash, "No se pudo preparar la copia ligera. Inténtalo otra vez en un momento.").ConfigureAwait(false);
            }
        }
        finally
        {
            _one.Release();
        }
    }

    private sealed class Throttled(ProxyShareHost host, string to, string mediaHash) : IProgress<double>
    {
        private long _last;

        public void Report(double value)
        {
            long now = Environment.TickCount64;
            if (now - Interlocked.Read(ref _last) < 500 && value < 1)
            {
                return;
            }

            Interlocked.Exchange(ref _last, now);
            UiTask.Run(host.TellAsync(to, mediaHash, Math.Clamp(value, 0, 1)));
        }
    }

    private Task TellAsync(string to, string mediaHash, double fraction) =>
        DirectShare.TrySendAsync(_room, ShareMessages.Make(ProxyMessages.Preparing, w =>
        {
            w.WriteString("sha256", mediaHash);
            w.WriteNumber("fraccion", fraction);
        }), to, _token);

    private Task Fail(string to, string mediaHash, string text) =>
        DirectShare.TrySendAsync(_room, ShareMessages.Make(ProxyMessages.Failed, w =>
        {
            w.WriteString("sha256", mediaHash);
            w.WriteString("texto", text);
        }), to, _token);
}

/// <summary>El lado del invitado: pide el proxy de un medio que no tiene, lo baja por huella y comprueba lo que llega.</summary>
public static class ProxyShareGuest
{
    public const int Steps = 2;

    /// <summary>
    /// Pide el proxy del medio cuyo archivo entero tiene este SHA-256 y lo deja en <paramref name="destination"/> (dentro de la
    /// carpeta de la sala). Devuelve la ruta. El tiempo de espera se reinicia con cada señal de vida del anfitrión.
    /// </summary>
    public static async Task<string> RequestAsync(RoomService room, string mediaHash, string destination, IProgress<ShareProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(room);
        if (!MediaManifest.IsHash(mediaHash))
        {
            throw new CollabException("Ese medio no se puede pedir.");
        }

        string host = room.HostKey;
        if (host.Length == 0 || room.State.IsHost)
        {
            throw new CollabException("Esto solo lo puede hacer quien se une a la sala de otra persona.");
        }

        bool? Classify(JsonElement message)
        {
            if (!DirectShare.IsAbout(message, mediaHash))
            {
                return null;
            }

            string? type = ShareMessages.Type(message);
            if (type is ProxyMessages.Ready or ProxyMessages.Failed)
            {
                return true;
            }

            if (type == ProxyMessages.Preparing && message.TryGetProperty("fraccion", out JsonElement f) && f.TryGetDouble(out double fraction) && double.IsFinite(fraction))
            {
                double clamped = Math.Clamp(fraction, 0, 1);
                progress?.Report(new ShareProgress(1, Steps, $"Quien hospeda la sala está preparando la copia ligera ({(int)(clamped * 100)} %)…", clamped));
                return false;
            }

            return null;
        }

        progress?.Report(new ShareProgress(1, Steps, "Pidiendo la copia ligera a quien hospeda la sala…", null));
        JsonElement reply = await DirectShare.AwaitReplyAsync(room, host, ShareMessages.Make(ProxyMessages.Ask, w => w.WriteString("sha256", mediaHash)), Classify,
            ProjectShareGuest.AnswerTimeout, "Quien hospeda la sala no mandó la copia ligera a tiempo. Comprueba que Rodaje sigue abierto allí y vuelve a pedirla.",
            cancellationToken).ConfigureAwait(false);

        if (ShareMessages.Type(reply) == ProxyMessages.Failed)
        {
            throw new CollabException(DirectShare.ErrorText(reply, "Quien hospeda la sala no pudo mandarte la copia ligera."));
        }

        if (!reply.TryGetProperty("huella", out JsonElement hh) || hh.GetString() is not { } hash || !MediaManifest.IsHash(hash)
            || !reply.TryGetProperty("tam", out JsonElement s) || !s.TryGetInt64(out long size) || size is <= 0 or > ProxyMessages.MaxProxyBytes)
        {
            throw new CollabException("Quien hospeda la sala mandó la copia ligera de una forma que Rodaje no entiende. Comprueba que las dos usáis la misma versión.");
        }

        void OnProgress(string blob, long bytes)
        {
            if (blob == hash)
            {
                double fraction = Math.Clamp((double)bytes / size, 0, 1);
                progress?.Report(new ShareProgress(2, Steps, $"Recibiendo la copia ligera ({(int)(fraction * 100)} %)…", fraction));
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        room.BlobProgress += OnProgress;
        try
        {
            progress?.Report(new ShareProgress(2, Steps, "Recibiendo la copia ligera (0 %)…", 0));
            await room.RequestBlobAsync(host, hash, size, destination, ProxyMessages.MaxProxyBytes, cancellationToken).ConfigureAwait(false);
            progress?.Report(new ShareProgress(2, Steps, "Recibiendo la copia ligera (100 %)…", 1));
        }
        finally
        {
            room.BlobProgress -= OnProgress;
        }

        FileInfo info = new(destination);
        if (!info.Exists || info.Length != size)
        {
            throw new CollabException("La copia ligera llegó dañada. Vuelve a pedirla.");
        }

        using FileStream stream = File.OpenRead(destination);
        if (Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)) != hash)
        {
            throw new CollabException("La copia ligera llegó dañada. Vuelve a pedirla.");
        }

        return destination;
    }
}
