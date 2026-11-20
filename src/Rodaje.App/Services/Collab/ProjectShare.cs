using System.Security.Cryptography;
using System.Text.Json;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;

namespace Rodaje.App.Services.Collab;

/// <summary>El proyecto del anfitrión tal como se manda: JSON del <c>.rodaje</c> sin rutas, su número de orden y los medios.</summary>
/// <param name="Seq">El último orden aplicado en el estado que se fotografió: el invitado sigue desde el siguiente.</param>
public sealed record ProjectSnapshot(string Name, byte[] Json, long Seq, IReadOnlyList<MediaManifestEntry> Media);

/// <summary>De dónde saca el anfitrión la fotografía del proyecto abierto. La implementación de la app la toma en el hilo de la interfaz.</summary>
public interface ICollabProjectSource
{
    Task<ProjectSnapshot> CaptureAsync(CancellationToken cancellationToken);
}

/// <summary>Lo que recibe el invitado: el proyecto ya sin rutas del anfitrión, su nombre y los medios que tiene que buscar.</summary>
public sealed record ReceivedProject(Project Project, string Name, long Seq, IReadOnlyList<MediaManifestEntry> Media, int DroppedFiles);

/// <summary>Los mensajes directos con los que el invitado pide el proyecto y el anfitrión contesta (fuera del orden de la sala).</summary>
internal static class ShareMessages
{
    public const string Ask = "pedir-proyecto";
    public const string Project = "proyecto";
    public const string Failed = "proyecto-error";
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
    private readonly RoomService _room;
    private readonly ICollabProjectSource _source;
    private readonly string _folder;
    private readonly SemaphoreSlim _one = new(1, 1);
    private readonly CancellationTokenSource _stop = new();

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
        if (ShareMessages.Type(message) == ShareMessages.Ask && _room.State is { IsHost: true, Phase: RoomPhase.Hosting })
        {
            _ = Task.Run(() => ServeAsync(from));
        }
    }

    private async Task ServeAsync(string to)
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
            ProjectSnapshot snapshot = await _source.CaptureAsync(_stop.Token).ConfigureAwait(false);
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
            _one.Release();
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

/// <summary>El lado del invitado: pide el proyecto, lo baja por huella, lo comprueba y le quita las rutas ajenas.</summary>
public static class ProjectShareGuest
{
    public static readonly TimeSpan AnswerTimeout = TimeSpan.FromSeconds(60);

    /// <param name="progress">De 0 a 1 mientras baja el proyecto.</param>
    public static async Task<ReceivedProject> ReceiveAsync(RoomService room, string roomFolder, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(room);
        string host = room.HostKey;
        if (host.Length == 0 || room.State.IsHost)
        {
            throw new CollabException("Esto solo lo puede hacer quien se une a la sala de otra persona.");
        }

        TaskCompletionSource<JsonElement> answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnDirect(string from, JsonElement message)
        {
            if (from == host && ShareMessages.Type(message) is ShareMessages.Project or ShareMessages.Failed)
            {
                answer.TrySetResult(message.Clone());
            }
        }

        room.DirectReceived += OnDirect;
        JsonElement reply;
        try
        {
            await room.SendDirectAsync(ShareMessages.Make(ShareMessages.Ask), host, cancellationToken).ConfigureAwait(false);
            reply = await answer.Task.WaitAsync(AnswerTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new CollabException("Quien hospeda la sala no mandó el proyecto a tiempo. Comprueba que Rodaje sigue abierto allí y vuelve a entrar.");
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
                progress?.Report(Math.Clamp((double)bytes / size, 0, 1));
            }
        }

        room.BlobProgress += OnProgress;
        try
        {
            await room.RequestBlobAsync(host, hash, size, received, ShareMessages.MaxProjectBytes, cancellationToken).ConfigureAwait(false);
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

        Project project;
        try
        {
            project = ProjectSerializer.Deserialize(System.Text.Encoding.UTF8.GetString(bytes));
        }
        catch (Exception ex) when (ex is ProjectFormatException or JsonException or InvalidOperationException or ArgumentException)
        {
            throw new CollabException("El proyecto que mandó la otra persona no se puede abrir con esta versión de Rodaje. Comprueba que las dos usáis la misma.", ex);
        }

        int dropped = ProjectScrub.Scrub(project);
        return new ReceivedProject(project, name, seq, media, dropped);
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
