using System.Security.Cryptography;
using System.Text.Json;
using Rodaje.Core.Collab;
using Rodaje.Core.Model;
using Rodaje.App.Services.Crashes;

namespace Rodaje.App.Services.Collab;

/// <summary>El proyecto del anfitrión tal como se manda: la copia inicial sin rutas (<c>ProjectWire</c>), su número de orden y los medios.</summary>
/// <param name="Seq">El último orden aplicado en el estado que se fotografió: el invitado sigue desde el siguiente.</param>
public sealed record ProjectSnapshot(string Name, byte[] Json, long Seq, IReadOnlyList<MediaManifestEntry> Media);

/// <summary>De dónde saca el anfitrión la fotografía del proyecto abierto. La implementación de la app la toma en el hilo de la interfaz.</summary>
public interface ICollabProjectSource
{
    /// <param name="progress">Medios con su SHA-256 calculado y medios en total (leer cada archivo entero lleva tiempo).</param>
    Task<ProjectSnapshot> CaptureAsync(IProgress<(int Done, int Total)>? progress, CancellationToken cancellationToken);
}

/// <summary>Lo que recibe el invitado: el proyecto sin rutas del anfitrión con sus versiones, su nombre, el orden en que está y los medios que tiene que buscar.</summary>
public sealed record ReceivedProject(Project Project, ElementVersions Versions, string Name, long Seq, IReadOnlyList<MediaManifestEntry> Media);

/// <summary>Los mensajes directos con los que el invitado pide el proyecto y el anfitrión contesta (fuera del orden de la sala).</summary>
internal static class ShareMessages
{
    public const string Ask = "pedir-proyecto";
    public const string Project = "proyecto";
    public const string Failed = "proyecto-error";
    public const string Preparing = "preparando";
    public const long MaxProjectBytes = 64L << 20;

    public static JsonElement Make(string type, Action<Utf8JsonWriter>? more = null)
    {
        using MemoryStream ms = new();
        using (Utf8JsonWriter w = new(ms))
        {
            w.WriteStartObject();
            w.WriteString("tipo", type);
            more?.Invoke(w);
            w.WriteEndObject();
        }

        using JsonDocument doc = JsonDocument.Parse(ms.ToArray());
        return doc.RootElement.Clone();
    }

    public static string? Type(JsonElement e) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty("tipo", out JsonElement t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;

    public static string Sha256(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));
}

/// <summary>
/// El lado del anfitrión: cuando un miembro pide el proyecto, lo fotografía, lo deja en la carpeta de la sala, lo ofrece por su
/// SHA-256 y le manda su nombre, su huella y la lista de medios. Un miembro cada vez; los demás esperan su turno.
/// </summary>
public sealed partial class ProjectShareHost : IDisposable
{
    /// <summary>Miembros a la vez con una petición esperando su turno o sirviéndose; las de más se rechazan en el acto.</summary>
    internal const int MaxPending = 8;

    private readonly RoomService _room;
    private readonly ICollabProjectSource _source;
    private readonly string _folder;
    private readonly SemaphoreSlim _one = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _gate = new();
    private readonly HashSet<string> _pending = new(StringComparer.Ordinal);

    public ProjectShareHost(RoomService room, ICollabProjectSource source, string roomFolder)
    {
        _room = room;
        _source = source;
        _folder = Path.Combine(Path.GetFullPath(roomFolder), "compartido");
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
        if (ShareMessages.Type(message) != ShareMessages.Ask || _room.State is not { IsHost: true, Phase: RoomPhase.Hosting or RoomPhase.Connected })
        {
            return;
        }

        lock (_gate)
        {
            if (_pending.Contains(from))
            {
                // Una petición pendiente por miembro: la que está en marcha ya sale con lo último confirmado.
                return;
            }

            if (_pending.Count >= MaxPending)
            {
                UiTask.Run(Task.Run(() => Fail(from, "Hay varias personas recibiendo el proyecto a la vez. Espera un momento y vuelve a pedirlo.")));
                return;
            }

            _pending.Add(from);
        }

        UiTask.Run(Task.Run(() => ServeAsync(from)));
    }

    private async Task ServeAsync(string to)
    {
        try
        {
            await _one.WaitAsync(_stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            lock (_gate)
            {
                _pending.Remove(to);
            }

            return;
        }

        try
        {
            if (!_room.State.Members.Any(m => m.Key == to && m.Online))
            {
                // Se fue mientras esperaba el turno: no se prepara una copia que nadie va a recibir.
                return;
            }

            ProjectSnapshot snapshot = await _source.CaptureAsync(new ThrottledProgress(this, to), _stop.Token).ConfigureAwait(false);
            if (snapshot.Json.Length is 0 or > (int)ShareMessages.MaxProjectBytes)
            {
                await Fail(to, "El proyecto es demasiado grande para compartirlo así. Comprime o simplifica el proyecto e inténtalo de nuevo.").ConfigureAwait(false);
                return;
            }

            string hash = ShareMessages.Sha256(snapshot.Json);
            Directory.CreateDirectory(_folder);
            string file = Path.Combine(_folder, hash + ".rodaje");
            if (!File.Exists(file))
            {
                await File.WriteAllBytesAsync(file, snapshot.Json, _stop.Token).ConfigureAwait(false);
            }

            await _room.OfferBlobAsync(file, hash, snapshot.Json.Length, _stop.Token).ConfigureAwait(false);
            await _room.SendDirectAsync(ShareMessages.Make(ShareMessages.Project, w =>
            {
                w.WriteString("nombre", snapshot.Name);
                w.WriteString("huella", hash);
                w.WriteNumber("tam", snapshot.Json.Length);
                w.WriteNumber("seq", snapshot.Seq);
                w.WriteStartArray("medios");
                foreach (MediaManifestEntry e in snapshot.Media.Take(MediaManifest.MaxEntries))
                {
                    w.WriteStartObject();
                    w.WriteString("id", e.MediaId);
                    w.WriteString("nombre", e.Name);
                    w.WriteNumber("tam", e.Size);
                    w.WriteString("sha256", e.Hash);
                    w.WriteEndObject();
                }

                w.WriteEndArray();
            }), to, _stop.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Nada de esto puede salir como excepción de una tarea suelta: el invitado recibe el motivo y la sala sigue.
            if (ex is not OperationCanceledException)
            {
                await Fail(to, "No se pudo preparar el proyecto para compartirlo. Comprueba que la carpeta del proyecto se puede escribir.").ConfigureAwait(false);
            }
        }
        finally
        {
            lock (_gate)
            {
                _pending.Remove(to);
            }

            _one.Release();
        }
    }

    /// <summary>Le cuenta al invitado cómo va la preparación de los medios, como mucho dos veces por segundo.</summary>
    private sealed class ThrottledProgress(ProjectShareHost host, string to) : IProgress<(int Done, int Total)>
    {
        private long _last;

        public void Report((int Done, int Total) value)
        {
            long now = Environment.TickCount64;
            if (now - Interlocked.Read(ref _last) < 500 && value.Done < value.Total)
            {
                return;
            }

            Interlocked.Exchange(ref _last, now);
            UiTask.Run(host.TellAsync(to, value.Done, value.Total));
        }
    }

    private async Task TellAsync(string to, int done, int total)
    {
        try
        {
            await _room.SendDirectAsync(ShareMessages.Make(ShareMessages.Preparing, w =>
            {
                w.WriteNumber("hecho", done);
                w.WriteNumber("total", total);
            }), to, _stop.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is CollabException or OperationCanceledException or ObjectDisposedException)
        {
            // Es un aviso de ánimo: si no sale, el invitado sigue viendo que la sala está viva.
        }
    }

    private async Task Fail(string to, string text)
    {
        try
        {
            await _room.SendDirectAsync(ShareMessages.Make(ShareMessages.Failed, w => w.WriteString("texto", text)), to, _stop.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is CollabException or OperationCanceledException)
        {
            // Si ni siquiera se puede avisar, el invitado verá que no llegó a tiempo.
        }
    }
}

/// <summary>Cómo va la recepción del proyecto, para una barra con porcentaje o por pasos.</summary>
/// <param name="Step">Paso actual (1 a <paramref name="Steps"/>).</param>
/// <param name="Fraction">De 0 a 1 dentro del paso, o <c>null</c> si no se sabe.</param>
public sealed record ShareProgress(int Step, int Steps, string Text, double? Fraction);

/// <summary>El lado del invitado: pide el proyecto, lo baja por huella, lo comprueba y le quita las rutas ajenas.</summary>
public static class ProjectShareGuest
{
    /// <summary>Sin ninguna señal de vida del anfitrión (ni el proyecto ni un «preparando») durante este tiempo, se da por perdido.</summary>
    public static readonly TimeSpan AnswerTimeout = TimeSpan.FromSeconds(60);

    public const int Steps = 3;

    public static async Task<ReceivedProject> ReceiveAsync(RoomService room, string roomFolder, IProgress<ShareProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(room);
        string host = room.HostKey;
        if (host.Length == 0 || room.State.IsHost)
        {
            throw new CollabException("Esto solo lo puede hacer quien se une a la sala de otra persona.");
        }

        TaskCompletionSource<JsonElement> answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        long lastSign = Environment.TickCount64;
        void OnDirect(string from, JsonElement message)
        {
            if (from != host)
            {
                return;
            }

            string? type = ShareMessages.Type(message);
            if (type is ShareMessages.Project or ShareMessages.Failed)
            {
                answer.TrySetResult(message.Clone());
            }
            else if (type == ShareMessages.Preparing
                && message.TryGetProperty("hecho", out JsonElement d) && d.TryGetInt32(out int done)
                && message.TryGetProperty("total", out JsonElement t) && t.TryGetInt32(out int total) && total > 0 && done >= 0 && done <= total)
            {
                Interlocked.Exchange(ref lastSign, Environment.TickCount64);
                progress?.Report(new ShareProgress(2, Steps, $"Quien hospeda la sala está preparando tus archivos ({done} de {total})…", (double)done / total));
            }
        }

        room.DirectReceived += OnDirect;
        JsonElement reply;
        try
        {
            progress?.Report(new ShareProgress(1, Steps, "Pidiendo el proyecto a quien hospeda la sala…", null));
            await room.SendDirectAsync(ShareMessages.Make(ShareMessages.Ask), host, cancellationToken).ConfigureAwait(false);
            while (!answer.Task.IsCompleted)
            {
                // Cada señal de vida del anfitrión reinicia la espera: preparar archivos enormes puede llevar minutos.
                TimeSpan left = AnswerTimeout - TimeSpan.FromMilliseconds(Environment.TickCount64 - Interlocked.Read(ref lastSign));
                if (left <= TimeSpan.Zero)
                {
                    throw new CollabException("Quien hospeda la sala no mandó el proyecto a tiempo. Comprueba que Rodaje sigue abierto allí y vuelve a entrar.");
                }

                await Task.WhenAny(answer.Task, Task.Delay(left < TimeSpan.FromSeconds(1) ? left : TimeSpan.FromSeconds(1), cancellationToken)).ConfigureAwait(false);
            }

            reply = answer.Task.Result;
        }
        finally
        {
            room.DirectReceived -= OnDirect;
        }

        if (ShareMessages.Type(reply) == ShareMessages.Failed)
        {
            string text = reply.TryGetProperty("texto", out JsonElement t) && t.ValueKind == JsonValueKind.String
                ? new string([.. (t.GetString() ?? "").Where(c => !char.IsControl(c)).Take(300)])
                : "";
            throw new CollabException(text.Length > 3 ? text : "Quien hospeda la sala no pudo mandarte el proyecto.");
        }

        (string name, string hash, long size, long seq) = ReadHeader(reply);
        IReadOnlyList<MediaManifestEntry> media = MediaManifest.Parse(ReWrap(reply));

        string received = Path.Combine(Path.GetFullPath(roomFolder), "recibido", hash + ".rodaje");
        Directory.CreateDirectory(Path.GetDirectoryName(received)!);
        void OnProgress(string h, long bytes)
        {
            if (h == hash)
            {
                double fraction = Math.Clamp((double)bytes / size, 0, 1);
                progress?.Report(new ShareProgress(3, Steps, $"Recibiendo el proyecto ({(int)(fraction * 100)} %)…", fraction));
            }
        }

        room.BlobProgress += OnProgress;
        try
        {
            progress?.Report(new ShareProgress(3, Steps, "Recibiendo el proyecto (0 %)…", 0));
            await room.RequestBlobAsync(host, hash, size, received, ShareMessages.MaxProjectBytes, cancellationToken).ConfigureAwait(false);
            progress?.Report(new ShareProgress(3, Steps, "Recibiendo el proyecto (100 %)…", 1));
        }
        finally
        {
            room.BlobProgress -= OnProgress;
        }

        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(received, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new CollabException("No se pudo leer el proyecto recibido. Comprueba que hay espacio en el disco y vuelve a entrar.", ex);
        }

        if (bytes.Length != size || ShareMessages.Sha256(bytes) != hash)
        {
            throw new CollabException("El proyecto llegó dañado. Vuelve a entrar en la sala.");
        }

        ProjectParseResult parsed = ProjectWire.TryParse(System.Text.Encoding.UTF8.GetString(bytes));
        if (parsed.Project is not { } project || parsed.Versions is not { } versions)
        {
            throw new CollabException("El proyecto que mandó la otra persona no se puede abrir con esta versión de Rodaje. Comprueba que las dos usáis la misma.");
        }

        project.Name = name;
        MediaPlaceholders.Fill(project);
        return new ReceivedProject(project, versions, name, seq, media);
    }

    private static (string Name, string Hash, long Size, long Seq) ReadHeader(JsonElement reply)
    {
        string? name = reply.TryGetProperty("nombre", out JsonElement n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
        string? hash = reply.TryGetProperty("huella", out JsonElement h) && h.ValueKind == JsonValueKind.String ? h.GetString() : null;
        if (name is null || hash is null || !MediaManifest.IsHash(hash)
            || !reply.TryGetProperty("tam", out JsonElement s) || !s.TryGetInt64(out long size) || size is < 2 or > ShareMessages.MaxProjectBytes)
        {
            throw new CollabException("Quien hospeda la sala mandó el proyecto de una forma que Rodaje no entiende. Comprueba que las dos usáis la misma versión.");
        }

        long seq = reply.TryGetProperty("seq", out JsonElement q) && q.TryGetInt64(out long v) && v >= 0 ? v : 0;
        return (ProjectFileName(name), hash, size, seq);
    }

    /// <summary>El nombre sin caracteres que Windows no admite en un archivo.</summary>
    public static string ProjectFileName(string name)
    {
        string clean = MediaManifest.DisplayName(name);
        clean = new string([.. clean.Where(c => Array.IndexOf(Path.GetInvalidFileNameChars(), c) < 0)]).Trim(' ', '.');
        return clean.Length is > 0 and <= 80 ? clean : clean.Length > 80 ? clean[..80].TrimEnd(' ', '.') : "Proyecto de la sala";
    }

    private static JsonElement ReWrap(JsonElement reply) =>
        // El mensaje del proyecto lleva la lista bajo «medios» como el manifiesto: se lee con el mismo lector.
        ShareMessages.Make(MediaManifest.Type, w =>
        {
            if (reply.TryGetProperty("medios", out JsonElement list))
            {
                w.WritePropertyName("medios");
                list.WriteTo(w);
            }
        });
}
