using System.Security.Cryptography;
using System.Text.Json;
using Dathinel.Sala;
using Rodaje.App.Services.Crashes;
using Rodaje.App.Services.Operations;
using Rodaje.App.Services.Packages;
using Rodaje.Core.Model;

namespace Rodaje.App.Services.Collab;

/// <summary>
/// De dónde saca el anfitrión el archivo original de un medio del proyecto compartido. Se busca por el SHA-256 del archivo
/// entero, calculado en este equipo: nunca por una ruta recibida.
/// </summary>
public interface IOriginalSource
{
    /// <summary>El original cuyo archivo entero tiene este SHA-256 (minúsculas); <c>null</c> si este equipo no lo tiene.</summary>
    Task<OriginalFile?> FindAsync(string mediaHash, CancellationToken cancellationToken);
}

/// <summary>Un original del anfitrión: su archivo, su tamaño y el nombre con que se ve en el proyecto.</summary>
public sealed record OriginalFile(string Path, long Size, string Name);

/// <summary>Lo que se pregunta a quien hospeda antes de mandar un original.</summary>
/// <param name="MemberKey">La clave de quien lo pide (no se enseña).</param>
public sealed record OriginalAsk(string MemberKey, string Alias, string MediaName, long Size)
{
    /// <summary>«Ana pide el original de clip-a (1,2 GB)».</summary>
    public string Text => $"{Alias} pide el original de {MediaName} ({PackageService.FormatSize(Size)}).";
}

/// <summary>Lo que respondió quien hospeda.</summary>
public enum OriginalDecision
{
    Refuse,
    Send,

    /// <summary>Enviar este y todos los que pida esa persona mientras dure la sala.</summary>
    SendAll,
}

/// <summary>Los mensajes directos de «Pedir el original» (fuera del orden de la sala).</summary>
internal static class OriginalMessages
{
    public const string Ask = "pedir-original";
    public const string Waiting = "original-esperando";
    public const string Ready = "original";
    public const string Failed = "original-error";
    public const string ChunkAsk = "pedir-trozo";
    public const string Chunk = "trozo";
    public const string Done = "original-fin";

    /// <summary>Trozos de 64 MiB: un corte solo repite el trozo en curso, y ninguno pasa del tope de 4 GiB de un blob de Sala.</summary>
    public const long ChunkBytes = 64L << 20;
    public const long MinChunkBytes = 64L << 10;
    public const long MaxChunkBytes = 1L << 30;
    public const long MaxOriginalBytes = 1L << 40;

    public static int ChunkCount(long size, long chunk) => (int)((size + chunk - 1) / chunk);

    public static long ChunkLength(long size, long chunk, int index) => Math.Min(chunk, size - (index * chunk));
}

/// <summary>
/// El lado del anfitrión de «Pedir el original»: un invitado pide el archivo original de un medio por el SHA-256 de su archivo
/// entero; quien hospeda lo aprueba (o no) y el archivo viaja en trozos, cada uno ofrecido como blob por su huella desde la
/// carpeta de la sala (Sala solo sirve archivos de ahí). Solo por conexión directa o relé propio, nunca por el relé público.
/// Un pedido pendiente por miembro y una cola global acotada; los trozos se preparan de uno en uno.
/// </summary>
public sealed partial class OriginalShareHost : IDisposable
{
    /// <summary>Lo que oye el invitado cuando el original solo podría viajar por el relé público.</summary>
    public const string RelayRefusal = "Por internet con el relé de pruebas no se mandan originales. Conectaos por la misma red o con un relé propio y vuelve a pedirlo.";

    /// <summary>Peticiones atendidas a la vez (preguntando o preparando trozos); las demás se niegan con «vuelve en un momento».</summary>
    public const int MaxQueued = 16;

    private const int KeptChunks = 4;

    private readonly RoomService _room;
    private readonly IOriginalSource _source;
    private readonly string _folder;
    private readonly Func<CancellationToken, Task<Diagnostico?>> _diagnostic;
    private readonly Func<OriginalAsk, CancellationToken, Task<OriginalDecision>>? _approve;
    private readonly Func<long?> _maxBytes;
    private readonly SemaphoreSlim _one = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationToken _token;
    private readonly Lock _gate = new();
    private readonly HashSet<string> _asking = new(StringComparer.Ordinal);
    private readonly HashSet<(string Member, string Hash)> _approved = [];
    private readonly HashSet<string> _sendAll = new(StringComparer.Ordinal);
    private readonly List<(string Hash, int Index, string Path, string Sha256, long Size)> _chunks = [];
    private int _queued;

    /// <param name="approve">Pregunta a quien hospeda; recibe un token que se cancela si pasa <see cref="ApprovalTimeout"/>. Sin ella se niega todo.</param>
    /// <param name="maxBytes">El tope de tamaño de Preferencias › Colaboración; <c>null</c>, sin tope.</param>
    /// <param name="diagnostic">Cómo leer el diagnóstico de la sala (el de <see cref="RoomService"/> salvo en las pruebas).</param>
    public OriginalShareHost(RoomService room, IOriginalSource source, string roomFolder,
        Func<OriginalAsk, CancellationToken, Task<OriginalDecision>>? approve,
        Func<long?>? maxBytes = null,
        Func<CancellationToken, Task<Diagnostico?>>? diagnostic = null)
    {
        ArgumentNullException.ThrowIfNull(room);
        ArgumentNullException.ThrowIfNull(source);
        _room = room;
        _source = source;
        _folder = Path.Combine(Path.GetFullPath(roomFolder), "originales");
        _approve = approve;
        _maxBytes = maxBytes ?? (() => null);
        _diagnostic = diagnostic ?? room.DiagnosticAsync;
        _token = _stop.Token;
        room.DirectReceived += OnDirect;
    }

    /// <summary>Si quien hospeda no responde en este tiempo, se niega.</summary>
    public TimeSpan ApprovalTimeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Cada cuánto se le dice al invitado que la pregunta sigue en pie (su espera se reinicia con cada aviso).</summary>
    public TimeSpan WaitingSignal { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Tamaño de cada trozo; las pruebas lo bajan para cortar a mitad sin mover gigas.</summary>
    public long ChunkBytes { get; init; } = OriginalMessages.ChunkBytes;

    public void Dispose()
    {
        _room.DirectReceived -= OnDirect;
        _stop.Cancel();
        _stop.Dispose();
        List<string> files;
        lock (_gate)
        {
            files = [.. _chunks.Select(c => c.Path)];
            _chunks.Clear();
        }

        foreach (string file in files)
        {
            TryDelete(file);
        }
    }

    private void OnDirect(string from, JsonElement message)
    {
        if (_room.State is not { IsHost: true, Phase: RoomPhase.Hosting }
            || !message.TryGetProperty("sha256", out JsonElement h) || h.ValueKind != JsonValueKind.String || h.GetString() is not { } hash || !MediaManifest.IsHash(hash))
        {
            return;
        }

        switch (ShareMessages.Type(message))
        {
            case OriginalMessages.Ask:
                UiTask.Run(Task.Run(() => AskAsync(from, hash)));
                break;
            case OriginalMessages.ChunkAsk when message.TryGetProperty("i", out JsonElement i) && i.TryGetInt32(out int index) && index >= 0:
                UiTask.Run(Task.Run(() => ChunkAsync(from, hash, index)));
                break;
            case OriginalMessages.Done:
                UiTask.Run(Task.Run(() => DropChunksAsync(hash)));
                break;
        }
    }

    private bool Enqueue()
    {
        if (Interlocked.Increment(ref _queued) <= MaxQueued)
        {
            return true;
        }

        Interlocked.Decrement(ref _queued);
        return false;
    }

    private const string Busy = "Quien hospeda la sala está atendiendo muchas peticiones a la vez. Vuelve a pedirlo en un momento.";

    private async Task AskAsync(string from, string hash)
    {
        bool first;
        lock (_gate)
        {
            first = _asking.Add(from);
        }

        if (!first)
        {
            await Fail(from, hash, "Ya tienes otro original pendiente de respuesta. Espera a que quien hospeda conteste y pídelo después.").ConfigureAwait(false);
            return;
        }

        if (!Enqueue())
        {
            lock (_gate)
            {
                _asking.Remove(from);
            }

            await Fail(from, hash, Busy).ConfigureAwait(false);
            return;
        }

        try
        {
            if (await OverPublicRelayAsync(from).ConfigureAwait(false))
            {
                await Fail(from, hash, RelayRefusal).ConfigureAwait(false);
                return;
            }

            OriginalFile? file = await FindAsync(hash).ConfigureAwait(false);
            if (file is null)
            {
                await Fail(from, hash, "Quien hospeda la sala no tiene ese archivo en su equipo, así que no puede mandar el original.").ConfigureAwait(false);
                return;
            }

            if (_maxBytes() is { } cap && file.Size > cap)
            {
                await Fail(from, hash, $"El original pesa {PackageService.FormatSize(file.Size)} y pasa del tope que quien hospeda puso en Preferencias › Colaboración ({PackageService.FormatSize(cap)}).").ConfigureAwait(false);
                return;
            }

            bool approved;
            lock (_gate)
            {
                approved = _approved.Contains((from, hash)) || _sendAll.Contains(from);
            }

            if (!approved && !await AskPersonAsync(from, hash, file).ConfigureAwait(false))
            {
                return;
            }

            lock (_gate)
            {
                _approved.Add((from, hash));
            }

            await _room.SendDirectAsync(ShareMessages.Make(OriginalMessages.Ready, w =>
            {
                w.WriteString("sha256", hash);
                w.WriteNumber("tam", file.Size);
                w.WriteNumber("trozo", ChunkBytes);
                w.WriteNumber("trozos", OriginalMessages.ChunkCount(file.Size, ChunkBytes));
                w.WriteString("ext", SafeExtension(file.Path));
            }), from, _token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (ex is not OperationCanceledException)
            {
                await Fail(from, hash, "No se pudo preparar el original. Inténtalo otra vez en un momento.").ConfigureAwait(false);
            }
        }
        finally
        {
            Interlocked.Decrement(ref _queued);
            lock (_gate)
            {
                _asking.Remove(from);
            }
        }
    }

    /// <summary>Pregunta a quien hospeda; mientras tanto, el invitado oye que la pregunta sigue en pie. Si dice que no o no contesta, se avisa.</summary>
    private async Task<bool> AskPersonAsync(string from, string hash, OriginalFile file)
    {
        if (_approve is not { } approve)
        {
            await Fail(from, hash, "Quien hospeda la sala no puede aprobar envíos ahora mismo. Vuelve a pedirlo más tarde.").ConfigureAwait(false);
            return false;
        }

        string alias = _room.State.Members.FirstOrDefault(m => m.Key == from)?.Alias ?? "Alguien";
        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(_token);
        limit.CancelAfter(ApprovalTimeout);
        Task signals = SignalWaitingAsync(from, hash, limit.Token);
        OriginalDecision? decision = null;
        try
        {
            decision = await approve(new OriginalAsk(from, alias, file.Name, file.Size), limit.Token).WaitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!_token.IsCancellationRequested)
        {
            // Sin respuesta en el plazo: se niega.
        }
        finally
        {
            await limit.CancelAsync().ConfigureAwait(false);
            await signals.ConfigureAwait(false);
        }

        if (decision is null)
        {
            await Fail(from, hash, $"Quien hospeda la sala no respondió en {OperationText.Duration(ApprovalTimeout)}, así que no se envió. Vuelve a pedirlo cuando esté delante de su equipo.").ConfigureAwait(false);
            return false;
        }

        if (decision == OriginalDecision.SendAll)
        {
            lock (_gate)
            {
                _sendAll.Add(from);
            }
        }

        if (decision == OriginalDecision.Refuse)
        {
            await Fail(from, hash, "Quien hospeda la sala decidió no enviarte este original.").ConfigureAwait(false);
            return false;
        }

        return true;
    }

    private async Task SignalWaitingAsync(string to, string hash, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await DirectShare.TrySendAsync(_room, ShareMessages.Make(OriginalMessages.Waiting, w => w.WriteString("sha256", hash)), to, cancellationToken).ConfigureAwait(false);
                await Task.Delay(WaitingSignal, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Se contestó o se acabó el plazo.
        }
    }

    private async Task ChunkAsync(string from, string hash, int index)
    {
        bool approved;
        lock (_gate)
        {
            approved = _approved.Contains((from, hash));
        }

        if (!approved)
        {
            await Fail(from, hash, "Quien hospeda la sala no aprobó el envío de este original. Vuelve a pedirlo.").ConfigureAwait(false);
            return;
        }

        if (!Enqueue())
        {
            await Fail(from, hash, Busy, retry: true).ConfigureAwait(false);
            return;
        }

        try
        {
            if (await OverPublicRelayAsync(from).ConfigureAwait(false))
            {
                await Fail(from, hash, RelayRefusal).ConfigureAwait(false);
                return;
            }

            await _one.WaitAsync(_token).ConfigureAwait(false);
            try
            {
                OriginalFile? file = await FindAsync(hash).ConfigureAwait(false);
                if (file is null)
                {
                    await Fail(from, hash, "Quien hospeda la sala ya no tiene ese archivo tal como estaba, así que no puede seguir mandándolo.").ConfigureAwait(false);
                    return;
                }

                if (index >= OriginalMessages.ChunkCount(file.Size, ChunkBytes))
                {
                    await Fail(from, hash, "Esa parte del original no existe. Comprueba que las dos usáis la misma versión de Rodaje.").ConfigureAwait(false);
                    return;
                }

                (string path, string sha, long size) = await ChunkFileAsync(file, hash, index).ConfigureAwait(false);
                await _room.OfferBlobAsync(path, sha, size, _token).ConfigureAwait(false);
                await _room.SendDirectAsync(ShareMessages.Make(OriginalMessages.Chunk, w =>
                {
                    w.WriteString("sha256", hash);
                    w.WriteNumber("i", index);
                    w.WriteString("huella", sha);
                    w.WriteNumber("tam", size);
                }), from, _token).ConfigureAwait(false);
            }
            finally
            {
                _one.Release();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (ex is not OperationCanceledException)
            {
                await Fail(from, hash, "No se pudo preparar una parte del original. Vuelve a pedirlo en un momento.", retry: true).ConfigureAwait(false);
            }
        }
        finally
        {
            Interlocked.Decrement(ref _queued);
        }
    }

    /// <summary>El trozo como archivo dentro de la carpeta de la sala, con su SHA-256; se guardan los últimos y se retiran los viejos.</summary>
    private async Task<(string Path, string Sha256, long Size)> ChunkFileAsync(OriginalFile file, string hash, int index)
    {
        lock (_gate)
        {
            foreach (var c in _chunks.Where(c => c.Hash == hash && c.Index == index && File.Exists(c.Path)))
            {
                return (c.Path, c.Sha256, c.Size);
            }
        }

        Directory.CreateDirectory(_folder);
        long length = OriginalMessages.ChunkLength(file.Size, ChunkBytes, index);
        string path = Path.Combine(_folder, $"{hash}-{index}.trozo");
        string temp = path + ".tmp";
        using IncrementalHash sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (FileStream input = new(file.Path, new FileStreamOptions
        {
            Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.ReadWrite | FileShare.Delete, BufferSize = 1 << 20, Options = FileOptions.Asynchronous,
        }))
        await using (FileStream output = new(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.Asynchronous))
        {
            input.Seek(index * ChunkBytes, SeekOrigin.Begin);
            byte[] buffer = new byte[1 << 20];
            long left = length;
            while (left > 0)
            {
                int read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, left)), _token).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new IOException("El original se acortó mientras se leía.");
                }

                sha.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), _token).ConfigureAwait(false);
                left -= read;
            }
        }

        File.Move(temp, path, overwrite: true);
        string digest = Convert.ToHexStringLower(sha.GetHashAndReset());
        List<(string Hash, int Index, string Path, string Sha256, long Size)> evicted;
        lock (_gate)
        {
            _chunks.RemoveAll(c => c.Path == path);
            _chunks.Add((hash, index, path, digest, length));
            evicted = _chunks.Count > KeptChunks ? _chunks.GetRange(0, _chunks.Count - KeptChunks) : [];
            _chunks.RemoveRange(0, evicted.Count);
        }

        await RetractAsync(evicted).ConfigureAwait(false);
        return (path, digest, length);
    }

    private async Task DropChunksAsync(string hash)
    {
        List<(string Hash, int Index, string Path, string Sha256, long Size)> gone;
        lock (_gate)
        {
            gone = [.. _chunks.Where(c => c.Hash == hash)];
            _chunks.RemoveAll(c => c.Hash == hash);
        }

        await RetractAsync(gone).ConfigureAwait(false);
    }

    private async Task RetractAsync(IEnumerable<(string Hash, int Index, string Path, string Sha256, long Size)> chunks)
    {
        foreach (var c in chunks)
        {
            try
            {
                await _room.RetractBlobAsync(c.Sha256).ConfigureAwait(false);
            }
            catch (CollabException)
            {
                // La sala ya no está: el archivo se borra igual.
            }

            TryDelete(c.Path);
        }
    }

    private async Task<OriginalFile?> FindAsync(string hash)
    {
        OriginalFile? file = await _source.FindAsync(hash, _token).ConfigureAwait(false);
        return file is { Size: > 0 and <= OriginalMessages.MaxOriginalBytes } && File.Exists(file.Path) ? file : null;
    }

    private async Task<bool> OverPublicRelayAsync(string peer) =>
        ProxyShareHost.OverPublicRelay(await DirectShare.ReadDiagnosticAsync(_diagnostic, _token).ConfigureAwait(false), peer);

    /// <summary>La extensión del original («.mp4») si es corta y solo de letras y números; vacía si no.</summary>
    private static string SafeExtension(string path)
    {
        string ext = Path.GetExtension(path);
        return OriginalNames.IsSafeExtension(ext) ? ext.ToLowerInvariant() : "";
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Se queda en la carpeta de la sala; no estorba.
        }
    }

    /// <param name="retry">Es pasajero (cola llena, un fallo al leer): el invitado puede reintentar solo.</param>
    private Task Fail(string to, string hash, string text, bool retry = false) =>
        DirectShare.TrySendAsync(_room, ShareMessages.Make(OriginalMessages.Failed, w =>
        {
            w.WriteString("sha256", hash);
            w.WriteString("texto", text);
            if (retry)
            {
                w.WriteBoolean("reintentar", true);
            }
        }), to, _token);
}

/// <summary>Los ajustes del lado del invitado; las pruebas los acortan.</summary>
public sealed record OriginalGuestOptions
{
    /// <summary>Reintentos de un trozo que se cortó (por la red o porque la sala se reconectaba) antes de rendirse.</summary>
    public int Retries { get; init; } = 6;

    /// <summary>Espera antes del primer reintento; se dobla en cada uno, hasta 30 s.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Sin respuesta ni señal de vida del anfitrión en este tiempo, se da por cortado.</summary>
    public TimeSpan AnswerTimeout { get; init; } = ProjectShareGuest.AnswerTimeout;

    /// <summary>Bytes libres en el disco de una carpeta; <c>null</c> si no se sabe (entonces no se comprueba).</summary>
    public Func<string, long?> FreeSpace { get; init; } = OriginalShareGuest.DiskFreeSpace;
}

/// <summary>
/// El lado del invitado: pide el original al anfitrión, lo baja por trozos (si se corta, sigue desde el último trozo entero,
/// también en otra petición), comprueba el SHA-256 del archivo entero contra el del manifiesto y lo deja con un nombre saneado
/// en la carpeta <c>medios</c> de la sala.
/// </summary>
public static class OriginalShareGuest
{
    public const int Steps = 3;

    /// <summary>La subcarpeta (dentro de <c>medios</c>) donde se guarda lo que va llegando, por huella, para poder seguir.</summary>
    public const string PartialFolder = ".parciales";

    /// <summary>
    /// Pide el original del medio del manifiesto y lo deja en <paramref name="mediaFolder"/> (dentro de la carpeta de la sala).
    /// Devuelve la ruta absoluta del archivo, ya comprobado.
    /// </summary>
    /// <exception cref="CollabException">No cabe, quien hospeda lo negó, no llegó o llegó distinto (se borra); el texto dice qué hacer.</exception>
    public static async Task<string> RequestAsync(RoomService room, MediaManifestEntry entry, string mediaFolder, IProgress<ShareProgress>? progress = null,
        OriginalGuestOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(room);
        ArgumentNullException.ThrowIfNull(entry);
        options ??= new OriginalGuestOptions();
        if (!MediaManifest.IsHash(entry.Hash) || entry.Size is <= 0 or > OriginalMessages.MaxOriginalBytes)
        {
            throw new CollabException("Ese medio no se puede pedir.");
        }

        string host = room.HostKey;
        if (host.Length == 0 || room.State.IsHost)
        {
            throw new CollabException("Esto solo lo puede hacer quien se une a la sala de otra persona.");
        }

        string folder = Path.GetFullPath(mediaFolder);
        string partials = Path.Combine(folder, PartialFolder);
        Directory.CreateDirectory(partials);
        string partial = Path.Combine(partials, entry.Hash + ".parcial");
        string chunkFile = Path.Combine(partials, entry.Hash + ".trozo");
        long have = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        if (have > entry.Size)
        {
            File.Delete(partial);
            have = 0;
        }

        CheckSpace(options, folder, entry.Size, have);

        progress?.Report(new ShareProgress(1, Steps, "Esperando a que quien hospeda la sala apruebe el envío…", null));
        JsonElement reply = await DirectShare.AwaitReplyAsync(room, host, ShareMessages.Make(OriginalMessages.Ask, w => w.WriteString("sha256", entry.Hash)),
            m => !DirectShare.IsAbout(m, entry.Hash) ? null : ShareMessages.Type(m) switch
            {
                OriginalMessages.Ready or OriginalMessages.Failed => true,
                OriginalMessages.Waiting => false,
                _ => null,
            },
            options.AnswerTimeout, "Quien hospeda la sala no contestó a tiempo. Comprueba que Rodaje sigue abierto allí y vuelve a pedirlo.", cancellationToken).ConfigureAwait(false);
        if (ShareMessages.Type(reply) == OriginalMessages.Failed)
        {
            throw new CollabException(DirectShare.ErrorText(reply, "Quien hospeda la sala no pudo mandarte el original."));
        }

        if (!reply.TryGetProperty("tam", out JsonElement t) || !t.TryGetInt64(out long size) || size != entry.Size
            || !reply.TryGetProperty("trozo", out JsonElement c) || !c.TryGetInt64(out long chunk) || chunk is < OriginalMessages.MinChunkBytes or > OriginalMessages.MaxChunkBytes
            || !reply.TryGetProperty("trozos", out JsonElement n) || !n.TryGetInt32(out int count) || count != OriginalMessages.ChunkCount(size, chunk))
        {
            throw new CollabException("Quien hospeda la sala mandó el original de una forma que Rodaje no entiende. Comprueba que las dos usáis la misma versión.");
        }

        string ext = reply.TryGetProperty("ext", out JsonElement e) && e.ValueKind == JsonValueKind.String && OriginalNames.IsSafeExtension(e.GetString()) ? e.GetString()! : "";

        // Lo que ya llegó en otra petición se conserva hasta el último trozo entero.
        have -= have % chunk;
        await using (FileStream trim = new(partial, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None))
        {
            trim.SetLength(have);
        }

        for (int index = (int)(have / chunk); index < count; index++)
        {
            await ReceiveChunkAsync(room, host, entry.Hash, index, OriginalMessages.ChunkLength(size, chunk, index), index * chunk, size,
                partial, chunkFile, progress, options, cancellationToken).ConfigureAwait(false);
        }

        progress?.Report(new ShareProgress(3, Steps, "Comprobando que el original es idéntico al de quien hospeda…", 0));
        if (!await MatchesAsync(partial, size, entry.Hash, progress, cancellationToken).ConfigureAwait(false))
        {
            TryDelete(partial);
            throw new CollabException("El original que llegó no es idéntico al de quien hospeda, así que se borró y no se enlazó. Vuelve a pedirlo; si se repite, puede que el archivo cambiara en su equipo.");
        }

        string final = OriginalNames.UniquePath(folder, OriginalNames.SafeFileName(entry.Name, ext));
        File.Move(partial, final);
        await DirectShare.TrySendAsync(room, ShareMessages.Make(OriginalMessages.Done, w => w.WriteString("sha256", entry.Hash)), host, CancellationToken.None).ConfigureAwait(false);
        progress?.Report(new ShareProgress(3, Steps, "Original comprobado.", 1));
        return final;
    }

    /// <summary>Bytes libres en el disco de <paramref name="folder"/>; <c>null</c> si no se puede leer.</summary>
    public static long? DiskFreeSpace(string folder)
    {
        try
        {
            return Path.GetPathRoot(Path.GetFullPath(folder)) is { Length: > 0 } root ? new DriveInfo(root).AvailableFreeSpace : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Hace falta lo que falta por bajar, un 5 % del tamaño de margen y sitio para el trozo en curso.</summary>
    private static void CheckSpace(OriginalGuestOptions options, string folder, long size, long have)
    {
        long left = size - have;
        long needed = left + (size / 20) + Math.Min(OriginalMessages.ChunkBytes, left);
        if (options.FreeSpace(folder) is { } free && free < needed)
        {
            throw new CollabException($"No cabe en el disco: el original pesa {PackageService.FormatSize(size)} y en el disco de la carpeta de la sala quedan {PackageService.FormatSize(free)} libres "
                + $"(hacen falta unos {PackageService.FormatSize(needed)}). Libera espacio en ese disco y vuelve a pedirlo.");
        }
    }

    private static async Task ReceiveChunkAsync(RoomService room, string host, string hash, int index, long length, long offset, long size,
        string partial, string chunkFile, IProgress<ShareProgress>? progress, OriginalGuestOptions options, CancellationToken cancellationToken)
    {
        string Receiving(double fraction) => $"Recibiendo el original ({(int)(fraction * 100)} %)…";
        for (int attempt = 0; ; attempt++)
        {
            bool refused = false;
            try
            {
                progress?.Report(new ShareProgress(2, Steps, Receiving((double)offset / size), (double)offset / size));
                JsonElement reply = await DirectShare.AwaitReplyAsync(room, host, ShareMessages.Make(OriginalMessages.ChunkAsk, w =>
                    {
                        w.WriteString("sha256", hash);
                        w.WriteNumber("i", index);
                    }),
                    m => !DirectShare.IsAbout(m, hash) ? null : ShareMessages.Type(m) switch
                    {
                        OriginalMessages.Failed => true,
                        OriginalMessages.Chunk => m.TryGetProperty("i", out JsonElement i) && i.TryGetInt32(out int at) && at == index ? true : null,
                        _ => null,
                    },
                    options.AnswerTimeout, "Quien hospeda la sala dejó de mandar el original.", cancellationToken).ConfigureAwait(false);
                if (ShareMessages.Type(reply) == OriginalMessages.Failed)
                {
                    refused = !(reply.TryGetProperty("reintentar", out JsonElement r) && r.ValueKind == JsonValueKind.True);
                    throw new CollabException(DirectShare.ErrorText(reply, "Quien hospeda la sala no pudo seguir mandando el original."));
                }

                if (!reply.TryGetProperty("huella", out JsonElement h) || h.ValueKind != JsonValueKind.String || h.GetString() is not { } sha || !MediaManifest.IsHash(sha)
                    || !reply.TryGetProperty("tam", out JsonElement s) || !s.TryGetInt64(out long got) || got != length)
                {
                    refused = true;
                    throw new CollabException("Quien hospeda la sala mandó una parte del original que no encaja. Comprueba que las dos usáis la misma versión.");
                }

                void OnProgress(string blob, long bytes)
                {
                    if (blob == sha)
                    {
                        double fraction = Math.Clamp((double)(offset + Math.Min(bytes, length)) / size, 0, 1);
                        progress?.Report(new ShareProgress(2, Steps, Receiving(fraction), fraction));
                    }
                }

                TryDelete(chunkFile);
                room.BlobProgress += OnProgress;
                try
                {
                    await room.RequestBlobAsync(host, sha, length, chunkFile, length, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    room.BlobProgress -= OnProgress;
                }

                await AppendAsync(partial, chunkFile, offset, length, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (CollabException) when (!refused && attempt < options.Retries && !cancellationToken.IsCancellationRequested)
            {
                TimeSpan wait = TimeSpan.FromTicks(Math.Min(TimeSpan.FromSeconds(30).Ticks, options.RetryDelay.Ticks << Math.Min(attempt, 10)));
                progress?.Report(new ShareProgress(2, Steps, $"Se cortó la conexión; Rodaje lo reintenta solo ({attempt + 1} de {options.Retries})…", (double)offset / size));
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Pega el trozo recibido al final de lo que ya había (en su sitio exacto) y lo borra.</summary>
    private static async Task AppendAsync(string partial, string chunkFile, long offset, long length, CancellationToken cancellationToken)
    {
        if (!File.Exists(chunkFile) || new FileInfo(chunkFile).Length != length)
        {
            throw new CollabException("Una parte del original llegó incompleta.");
        }

        await using (FileStream output = new(partial, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.Asynchronous))
        await using (FileStream input = new(chunkFile, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            output.SetLength(offset);
            output.Seek(offset, SeekOrigin.Begin);
            await input.CopyToAsync(output, 1 << 20, cancellationToken).ConfigureAwait(false);
        }

        TryDelete(chunkFile);
    }

    private static async Task<bool> MatchesAsync(string path, long size, string hash, IProgress<ShareProgress>? progress, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length != size)
        {
            return false;
        }

        using IncrementalHash sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[1 << 20];
        long done = 0;
        long lastReport = Environment.TickCount64;
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            sha.AppendData(buffer, 0, read);
            done += read;
            if (Environment.TickCount64 - lastReport > 250)
            {
                lastReport = Environment.TickCount64;
                progress?.Report(new ShareProgress(3, Steps, "Comprobando que el original es idéntico al de quien hospeda…", (double)done / size));
            }
        }

        return Convert.ToHexStringLower(sha.GetHashAndReset()) == hash;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Lo que no se pudo borrar se sobrescribe en el próximo intento.
        }
    }
}

/// <summary>Nombres de archivo para lo que llega de otro equipo: el nombre del manifiesto es ajeno.</summary>
public static class OriginalNames
{
    private static readonly string[] Reserved =
        ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];

    /// <summary>«.mp4», «.mov», «.braw»: un punto y de 1 a 10 letras o números ASCII.</summary>
    public static bool IsSafeExtension(string? ext) =>
        ext is { Length: >= 2 and <= 11 } && ext[0] == '.' && ext.Skip(1).All(char.IsAsciiLetterOrDigit);

    /// <summary>
    /// Un nombre de archivo sin carpetas, sin <c>..</c>, sin caracteres que Windows no admite, sin nombres reservados y con
    /// la extensión del original si no la trae.
    /// </summary>
    public static string SafeFileName(string name, string extension)
    {
        string ext = IsSafeExtension(extension) ? extension.ToLowerInvariant() : "";
        char[] invalid = Path.GetInvalidFileNameChars();
        string clean = new([.. MediaManifest.DisplayName(name ?? "").Select(c => Array.IndexOf(invalid, c) >= 0 || c is '/' or '\\' or ':' ? '_' : c)]);
        clean = clean.Trim().Trim('.', ' ');
        if (ext.Length > 0 && clean.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
        {
            clean = clean[..^ext.Length].TrimEnd('.', ' ');
        }

        if (clean.Length > 120)
        {
            clean = clean[..120].TrimEnd('.', ' ');
        }

        string stem = clean.Split('.')[0];
        if (clean.Length == 0 || clean.All(c => c == '_'))
        {
            clean = "medio";
        }
        else if (Reserved.Contains(stem, StringComparer.OrdinalIgnoreCase))
        {
            clean = "_" + clean;
        }

        return clean + ext;
    }

    /// <summary>La ruta dentro de <paramref name="folder"/>; si ya existe, «nombre (2).ext», «nombre (3).ext»…</summary>
    public static string UniquePath(string folder, string fileName)
    {
        string full = Path.GetFullPath(folder);
        string stem = Path.GetFileNameWithoutExtension(fileName);
        string ext = Path.GetExtension(fileName);
        string candidate = Path.Combine(full, fileName);
        for (int i = 2; File.Exists(candidate) || Directory.Exists(candidate); i++)
        {
            candidate = Path.Combine(full, $"{stem} ({i}){ext}");
        }

        if (!DirectShare.IsInside(full, candidate))
        {
            throw new CollabException("El nombre del original no es válido.");
        }

        return candidate;
    }
}

/// <summary>
/// El <see cref="IOriginalSource"/> de verdad: busca entre los medios del proyecto del anfitrión el que tiene ese SHA-256 entero
/// (con la caché por ruta, tamaño y fecha de <see cref="MediaManifestBuilder"/>). Una búsqueda cada vez.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "SemaphoreSlim sin AvailableWaitHandle no reserva nada que liberar.")]
public sealed class ProjectOriginalSource(Func<IReadOnlyList<MediaRef>> media, Func<MediaRef, string> resolvePath, MediaManifestBuilder? builder = null) : IOriginalSource
{
    private readonly MediaManifestBuilder _builder = builder ?? new MediaManifestBuilder();
    private readonly SemaphoreSlim _one = new(1, 1);

    public async Task<OriginalFile?> FindAsync(string mediaHash, CancellationToken cancellationToken)
    {
        await _one.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await _builder.FindMediaByHashAsync(media(), resolvePath, mediaHash, cancellationToken).ConfigureAwait(false) is not { } found)
            {
                return null;
            }

            string name = string.IsNullOrWhiteSpace(found.Media.Name) ? MediaManifest.DisplayName(found.Path) : MediaManifest.DisplayName(found.Media.Name);
            return new OriginalFile(found.Path, new FileInfo(found.Path).Length, name);
        }
        finally
        {
            _one.Release();
        }
    }
}
