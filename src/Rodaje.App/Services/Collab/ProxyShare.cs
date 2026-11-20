using System.Security.Cryptography;
using System.Text.Json;

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
    private readonly SemaphoreSlim _one = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<string, RoomProxy> _made = new(StringComparer.Ordinal);

    public ProxyShareHost(RoomService room, IRoomProxyMaker maker, string roomFolder)
    {
        _room = room;
        _maker = maker;
        _folder = Path.Combine(Path.GetFullPath(roomFolder), "proxies");
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
            _ = Task.Run(() => ServeAsync(from, hash));
        }
    }

    private async Task ServeAsync(string to, string mediaHash)
    {
        try
        {
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
                proxy = await _maker.MakeAsync(mediaHash, _folder, new Throttled(this, to, mediaHash), _stop.Token).ConfigureAwait(false);
                if (proxy is null)
                {
                    await Fail(to, mediaHash, "Quien hospeda la sala no tiene ese archivo, así que no puede hacer su copia ligera.").ConfigureAwait(false);
                    return;
                }

                if (!IsInside(_folder, proxy.Path) || proxy.Size is <= 0 or > ProxyMessages.MaxProxyBytes || !MediaManifest.IsHash(proxy.Sha256))
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

    private static bool IsInside(string folder, string path)
    {
        string full = Path.GetFullPath(path);
        return full.StartsWith(Path.GetFullPath(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
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
            _ = host.TellAsync(to, mediaHash, Math.Clamp(value, 0, 1));
        }
    }

    private async Task TellAsync(string to, string mediaHash, double fraction)
    {
        try
        {
            await _room.SendDirectAsync(ShareMessages.Make(ProxyMessages.Preparing, w =>
            {
                w.WriteString("sha256", mediaHash);
                w.WriteNumber("fraccion", fraction);
            }), to, _stop.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is CollabException or OperationCanceledException or ObjectDisposedException)
        {
            // Es un aviso de ánimo.
        }
    }

    private async Task Fail(string to, string mediaHash, string text)
    {
        try
        {
            await _room.SendDirectAsync(ShareMessages.Make(ProxyMessages.Failed, w =>
            {
                w.WriteString("sha256", mediaHash);
                w.WriteString("texto", text);
            }), to, _stop.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is CollabException or OperationCanceledException or ObjectDisposedException)
        {
            // Si ni siquiera se puede avisar, el invitado verá que no llegó a tiempo.
        }
    }
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

        TaskCompletionSource<JsonElement> answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        long lastSign = Environment.TickCount64;
        void OnDirect(string from, JsonElement message)
        {
            if (from != host || !message.TryGetProperty("sha256", out JsonElement h) || h.GetString() != mediaHash)
            {
                return;
            }

            string? type = ShareMessages.Type(message);
            if (type is ProxyMessages.Ready or ProxyMessages.Failed)
            {
                answer.TrySetResult(message.Clone());
            }
            else if (type == ProxyMessages.Preparing && message.TryGetProperty("fraccion", out JsonElement f) && f.TryGetDouble(out double fraction) && double.IsFinite(fraction))
            {
                Interlocked.Exchange(ref lastSign, Environment.TickCount64);
                double clamped = Math.Clamp(fraction, 0, 1);
                progress?.Report(new ShareProgress(1, Steps, $"Quien hospeda la sala está preparando la copia ligera ({(int)(clamped * 100)} %)…", clamped));
            }
        }

        room.DirectReceived += OnDirect;
        JsonElement reply;
        try
        {
            progress?.Report(new ShareProgress(1, Steps, "Pidiendo la copia ligera a quien hospeda la sala…", null));
            await room.SendDirectAsync(ShareMessages.Make(ProxyMessages.Ask, w => w.WriteString("sha256", mediaHash)), host, cancellationToken).ConfigureAwait(false);
            while (!answer.Task.IsCompleted)
            {
                TimeSpan left = ProjectShareGuest.AnswerTimeout - TimeSpan.FromMilliseconds(Environment.TickCount64 - Interlocked.Read(ref lastSign));
                if (left <= TimeSpan.Zero)
                {
                    throw new CollabException("Quien hospeda la sala no mandó la copia ligera a tiempo. Comprueba que Rodaje sigue abierto allí y vuelve a pedirla.");
                }

                await Task.WhenAny(answer.Task, Task.Delay(left < TimeSpan.FromSeconds(1) ? left : TimeSpan.FromSeconds(1), cancellationToken)).ConfigureAwait(false);
            }

            reply = answer.Task.Result;
        }
        finally
        {
            room.DirectReceived -= OnDirect;
        }

        if (ShareMessages.Type(reply) == ProxyMessages.Failed)
        {
            string text = reply.TryGetProperty("texto", out JsonElement t) && t.ValueKind == JsonValueKind.String
                ? new string([.. (t.GetString() ?? "").Where(c => !char.IsControl(c)).Take(300)])
                : "";
            throw new CollabException(text.Length > 3 ? text : "Quien hospeda la sala no pudo mandarte la copia ligera.");
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
