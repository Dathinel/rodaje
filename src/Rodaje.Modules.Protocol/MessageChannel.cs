using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rodaje.Modules.Protocol;

public static class ProtocolVersion
{
    /// <summary>La versión que habla esta biblioteca.</summary>
    public const int Current = 1;

    /// <summary>Tamaño máximo de un mensaje; cuadros y audio van por memoria compartida.</summary>
    public const int MaxFrameBytes = 1 << 20;
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    AllowOutOfOrderMetadataProperties = true,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true,
    MaxDepth = 32)]
[JsonSerializable(typeof(ModuleMessage))]
internal sealed partial class ProtocolJsonContext : JsonSerializerContext;

/// <summary>
/// Mensajes con marco: 4 bytes little-endian con el largo y luego el JSON en UTF-8. Las escrituras se serializan; la
/// lectura es de un solo lector. Un marco inválido lanza <see cref="ModuleProtocolException"/>: la conexión ya no es fiable.
/// </summary>
internal sealed class MessageChannel(Stream stream, int maxFrameBytes = ProtocolVersion.MaxFrameBytes) : IDisposable
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly byte[] _header = new byte[4];

    /// <summary>Mensajes con un <c>type</c> desconocido que se saltaron (compatibilidad hacia delante).</summary>
    public int SkippedUnknown { get; private set; }

    /// <summary>
    /// Una escritura se interrumpió a medias (cancelada o pasado su plazo): el marco pudo quedar cortado, así que el flujo
    /// ya se cerró y la conexión no sirve.
    /// </summary>
    public bool Broken { get; private set; }

    /// <summary>Se cerró el flujo (<see cref="Dispose"/> o una escritura cortada).</summary>
    private volatile bool _closed;

    /// <summary>Escribe un marco sin plazo de escritura.</summary>
    /// <exception cref="ModuleProtocolException">El mensaje pasa del máximo; no se escribió nada.</exception>
    public ValueTask SendAsync(ModuleMessage message, CancellationToken cancellationToken = default) => SendAsync(message, null, cancellationToken);

    /// <summary>
    /// Escribe un marco. <paramref name="cancellationToken"/> vale para la espera del candado y para la escritura; si
    /// corta la escritura, o si esta pasa de <paramref name="writeTimeout"/> (contado desde que se tiene el candado), el
    /// marco pudo quedar a medias: se cierra el flujo, se marca <see cref="Broken"/> y sale
    /// <see cref="OperationCanceledException"/> o, por el plazo, <see cref="TimeoutException"/>.
    /// </summary>
    /// <exception cref="ModuleProtocolException">El mensaje pasa del máximo; no se escribió nada.</exception>
    public async ValueTask SendAsync(ModuleMessage message, TimeSpan? writeTimeout, CancellationToken cancellationToken)
    {
        ArrayBufferWriter<byte> buffer = new(256);
        buffer.GetSpan(4);
        buffer.Advance(4);
        using (Utf8JsonWriter writer = new(buffer))
            JsonSerializer.Serialize(writer, message, ProtocolJsonContext.Default.ModuleMessage);
        int length = buffer.WrittenCount - 4;
        if (length > maxFrameBytes) throw new ModuleProtocolException($"Mensaje de {length} bytes: el máximo es {maxFrameBytes}.");
        byte[] frame = buffer.WrittenSpan.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(frame, length);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using CancellationTokenSource? limit = writeTimeout.HasValue ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken) : null;
            if (writeTimeout is { } after) limit!.CancelAfter(after);
            CancellationToken token = limit?.Token ?? cancellationToken;
            try
            {
                await stream.WriteAsync(frame, token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex) when (_closed)
            {
                throw Closed(ex);
            }
            catch (OperationCanceledException ex) when (token.IsCancellationRequested)
            {
                // El marco pudo quedar a medias: lo que siga en el flujo ya no sería un marco válido.
                Broken = true;
                _closed = true;
                Close(stream);
                if (!cancellationToken.IsCancellationRequested)
                    throw new TimeoutException($"Una escritura no terminó en {writeTimeout!.Value.TotalSeconds:0.#} s: el otro lado no lee.", ex);
                throw;
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <returns><c>null</c> si el otro lado cerró entre dos mensajes.</returns>
    public async ValueTask<ModuleMessage?> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            if (!await ReadExactlyOrEndAsync(_header, cancellationToken).ConfigureAwait(false)) return null;
            int length = BinaryPrimitives.ReadInt32LittleEndian(_header);
            if (length <= 0 || length > maxFrameBytes)
                throw new ModuleProtocolException($"Marco de {length} bytes: el máximo es {maxFrameBytes}.");
            byte[] payload = ArrayPool<byte>.Shared.Rent(length);
            try
            {
                if (!await ReadExactlyOrEndAsync(payload.AsMemory(0, length), cancellationToken).ConfigureAwait(false))
                    throw new ModuleProtocolException("La conexión se cortó a mitad de un mensaje.");
                if (Parse(payload.AsSpan(0, length)) is { } message) return message;
                SkippedUnknown++;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(payload);
            }
        }
    }

    /// <returns><c>null</c> si es un tipo de mensaje que esta versión no conoce.</returns>
    internal static ModuleMessage? Parse(ReadOnlySpan<byte> json)
    {
        ModuleMessage? message;
        try
        {
            message = JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.ModuleMessage);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException or ArgumentException)
        {
            if (IsUnknownType(json)) return null;
            throw new ModuleProtocolException("Mensaje malformado: " + ex.Message, ex);
        }
        if (message is null) throw new ModuleProtocolException("Mensaje malformado: null.");
        if (message.Validate() is { } reason) throw new ModuleProtocolException($"Mensaje «{message.GetType().Name}» inválido: {reason}.");
        return message;
    }

    private static bool IsUnknownType(ReadOnlySpan<byte> json)
    {
        try
        {
            Utf8JsonReader reader = new(json, new JsonReaderOptions { MaxDepth = 32 });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return false;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                bool isType = reader.ValueTextEquals("type"u8);
                if (!reader.Read()) return false;
                if (isType)
                    return reader.TokenType == JsonTokenType.String && reader.GetString() is { } t
                        && Limits.Identifier(t) is null && !ModuleMessage.KnownTypes.Contains(t)
                        && IsCompleteJson(json);
                reader.Skip();
            }
            return false;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Si el marco entero es un único valor JSON válido en UTF-8: un tipo desconocido solo se salta si lo es; cortado, con
    /// basura detrás o con UTF-8 roto es malformado y corta la conexión, como cualquier otro marco.
    /// </summary>
    private static bool IsCompleteJson(ReadOnlySpan<byte> json)
    {
        if (!System.Text.Unicode.Utf8.IsValid(json)) return false;
        try
        {
            Utf8JsonReader reader = new(json, new JsonReaderOptions { MaxDepth = 32 });
            while (reader.Read())
            {
            }
            return reader.BytesConsumed > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async ValueTask<bool> ReadExactlyOrEndAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n;
            try
            {
                n = await stream.ReadAsync(buffer[read..], cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex) when (_closed)
            {
                throw Closed(ex);
            }
            if (n == 0)
            {
                if (read == 0) return false;
                throw new ModuleProtocolException("La conexión se cortó a mitad de un mensaje.");
            }
            read += n;
        }
        return true;
    }

    /// <summary>
    /// Cierra el flujo. El candado de escritura no se desecha: <see cref="SemaphoreSlim.Dispose()"/> vacía su cola sin
    /// despertar a quien espera, y una escritura que esperaba con un token ya cancelado (por ejemplo <c>_stop</c> de
    /// <see cref="ModuleClient"/>, que se cancela justo antes de llegar aquí) se quedaba colgada para siempre. Sin desecharlo,
    /// esa espera termina con su cancelación y una escritura posterior falla con el flujo ya cerrado. El semáforo no tiene
    /// recursos que liberar mientras nadie pida <see cref="SemaphoreSlim.AvailableWaitHandle"/>. Quien tiene el candado
    /// tampoco lo retiene para siempre si el otro lado deja de leer: <see cref="ModuleClient"/> escribe con un plazo
    /// (<c>writeTimeout</c>) y con el token del llamador, y al vencer falla y cierra (defecto 5 de <c>ProtocolDefectTests</c>).
    /// </summary>
    public void Dispose()
    {
        _closed = true;
        stream.Dispose();
    }

    /// <summary>
    /// Corta la conexión por un fallo, sin cierre ordenado: como <see cref="Dispose"/>, pero desconectando antes el lado
    /// servidor de la tubería (<see cref="Close(Stream)"/>). El cierre normal (tras <c>shutdown</c>) sigue siendo
    /// <see cref="Dispose"/>: desconectar descarta lo que el módulo no leyó, y el módulo de eco salía con 2 en vez de 0
    /// porque perdía el <c>shutdown</c> a medio leer. Y ese <see cref="Dispose"/> llega solo cuando el módulo ya cerró su
    /// lado (o se agotó <see cref="ModuleClientOptions.ShutdownGrace"/>): cerrar antes también lo perdía (tarea 189).
    /// </summary>
    public void Abort()
    {
        _closed = true;
        Close(stream);
    }

    /// <summary>
    /// Entre <c>Disconnect</c> y <c>Dispose</c> de <see cref="Close(Stream)"/>, una lectura o escritura de otro hilo ve la
    /// tubería «desconectada» y lanza <see cref="InvalidOperationException"/>; para quien usa el canal es lo mismo que un
    /// flujo ya cerrado.
    /// </summary>
    private static ObjectDisposedException Closed(InvalidOperationException ex) => new("La conexión ya se cerró.", ex);

    /// <summary>
    /// Cierra un flujo de la conexión. Si es el lado servidor de una tubería con nombre (el editor), primero la desconecta
    /// (<c>DisconnectNamedPipe</c>). En Windows, solo cerrar el asa con una lectura propia pendiente (el bucle de lectura
    /// de <see cref="ModuleClient"/>) y datos aún sin leer del otro lado deja a veces la lectura pendiente del módulo sin
    /// completar nunca: ni datos ni fin de flujo, aunque una escritura del módulo ya falla con «Pipe is broken». El módulo
    /// no se enteraba del corte y seguía vivo hasta que lo mataran. Lo cazó la CI de Windows (tarea 137): con la CPU
    /// llena, 2 de cada 9 rondas de <c>Cancel_racing_the_end_of_a_call_does_not_close_the_connection</c> solo con
    /// <c>Dispose</c>, y ninguna desconectando antes. Desconectar descarta lo que el módulo no leyó y le completa la
    /// lectura con fin de flujo; por eso solo se usa al cortar por un fallo (<see cref="Abort"/>).
    /// </summary>
    internal static void Close(Stream stream)
    {
        if (stream is NamedPipeServerStream { IsConnected: true } server)
        {
            try
            {
                server.Disconnect();
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
            {
                // Ya se había cortado o cerrado: basta con desechar.
            }
        }
        stream.Dispose();
    }
}
