using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;

namespace Rodaje.Core.Collab;

/// <summary>
/// El contenido de red de cada elemento: el JSON del <c>.rodaje</c> de ese elemento, compacto, con las claves ordenadas, sin la
/// ruta de los medios y con las referencias <c>file:</c> cambiadas por <c>blob:&lt;sha256&gt;</c> (o vacías sin <see cref="IBlobMap"/>).
/// Dos elementos iguales dan siempre el mismo texto, en cualquier equipo.
/// </summary>
internal static class ElementCodec
{
    public const string BlobPrefix = "blob:";

    private const string OptionsKey = "options";
    private const string MediaPathKey = "path";

    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly JsonDocumentOptions ParseOptions = new() { MaxDepth = 64, AllowDuplicateProperties = false };

    private static JsonTypeInfo<T> Info<T>(JsonSerializerOptions options) => (JsonTypeInfo<T>)options.GetTypeInfo(typeof(T));

    public static string EncodeMedia(MediaRef media, IBlobMap? blobs) => Encode(media, MediaPathKey, blobs);

    // La fecha de creación se escribe siempre en UTC: con un desfase local, dos equipos en husos distintos darían textos distintos del
    // mismo instante. Una fecha sin zona (Unspecified) se toma como UTC, nunca como hora local, para que tampoco dependa del equipo.
    public static string EncodeTranscript(MediaTranscript transcript, IBlobMap? blobs)
    {
        if (transcript.Created.Kind == DateTimeKind.Utc)
        {
            return Encode(transcript, null, blobs);
        }

        MediaTranscript copy = transcript.Clone();
        copy.Created = AsUtc(transcript.Created);
        return Encode(copy, null, blobs);
    }

    public static string EncodeSequence(Sequence sequence, IBlobMap? blobs) => Encode(sequence.WithoutContents(), null, blobs);

    public static string EncodeTrack(Track track, IBlobMap? blobs) => Encode(track.WithoutClips(), null, blobs);

    public static string EncodeClip(Clip clip, IBlobMap? blobs) => Encode(clip, null, blobs);

    public static string EncodeMarker(Marker marker) => Encode(marker, null, null);

    public static string EncodeRegion(Region region) => Encode(region, null, null);

    private static string Encode<T>(T value, string? dropKey, IBlobMap? blobs)
    {
        try
        {
            JsonNode? node = JsonSerializer.SerializeToNode(value, Info<T>(ProjectJson.ElementWriteOptions));
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, WriterOptions))
            {
                Write(writer, node, dropKey, inOptions: false, blobs);
            }

            return Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
        }
        catch (JsonException ex)
        {
            throw new ProjectFormatException(
                $"No se puede compartir el elemento «{(value as object)?.GetType().Name}»: tiene un valor que el archivo no puede guardar ({ex.Message}).",
                ex);
        }
    }

    private static void Write(Utf8JsonWriter writer, JsonNode? node, string? dropKey, bool inOptions, IBlobMap? blobs)
    {
        switch (node)
        {
            case null:
                writer.WriteNullValue();
                break;
            case JsonObject obj:
                writer.WriteStartObject();
                foreach (KeyValuePair<string, JsonNode?> property in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    if (dropKey is not null && property.Key == dropKey)
                    {
                        continue;
                    }

                    writer.WritePropertyName(property.Key);
                    Write(writer, property.Value, null, inOptions: inOptions || property.Key == OptionsKey, blobs);
                }

                writer.WriteEndObject();
                break;
            case JsonArray array:
                writer.WriteStartArray();
                foreach (JsonNode? item in array)
                {
                    Write(writer, item, null, inOptions: false, blobs);
                }

                writer.WriteEndArray();
                break;
            case JsonValue value:
                if (inOptions && value.TryGetValue(out string? text) && text is not null && text.StartsWith(Effects.OptionDefinition.FilePrefix, StringComparison.Ordinal))
                {
                    string? sha = blobs?.Sha256Of(text);
                    writer.WriteStringValue(ContentHash.IsValid(sha) ? BlobPrefix + sha : "");
                }
                else
                {
                    value.WriteTo(writer, ProjectJson.ElementWriteOptions);
                }

                break;
        }
    }

    public static MediaRef? DecodeMedia(string content, IBlobMap? blobs, out string? error) => Decode<MediaRef>(content, blobs, out error);

    public static MediaTranscript? DecodeTranscript(string content, IBlobMap? blobs, out string? error)
    {
        MediaTranscript? transcript = Decode<MediaTranscript>(content, blobs, out error);
        if (transcript is not null)
        {
            transcript.Created = AsUtc(transcript.Created);
        }

        return transcript;
    }

    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    public static Sequence? DecodeSequence(string content, IBlobMap? blobs, out string? error) => Decode<Sequence>(content, blobs, out error);

    public static Track? DecodeTrack(string content, IBlobMap? blobs, out string? error) => Decode<Track>(content, blobs, out error);

    public static Clip? DecodeClip(string content, IBlobMap? blobs, out string? error) => Decode<Clip>(content, blobs, out error);

    public static Marker? DecodeMarker(string content, out string? error) => Decode<Marker>(content, null, out error);

    public static Region? DecodeRegion(string content, out string? error) => Decode<Region>(content, null, out error);

    /// <summary>
    /// Lee el contenido de un elemento. Las referencias <c>blob:</c> pasan a <c>file:</c> si <paramref name="blobs"/> conoce el
    /// archivo, o quedan vacías; un <c>file:</c> que llega de fuera nunca se acepta. Devuelve <see langword="null"/> y el motivo
    /// en español si el texto no es un elemento de esa clase (un motivo que empieza por «!» es hostil); no lanza.
    /// </summary>
    private static T? Decode<T>(string content, IBlobMap? blobs, out string? error)
        where T : class
    {
        error = null;
        try
        {
            if (JsonNode.Parse(content, nodeOptions: null, ParseOptions) is not JsonObject root)
            {
                error = "El contenido de un elemento no es un objeto JSON.";
                return null;
            }

            if (!RewriteBlobs(root, inOptions: false, blobs, out error))
            {
                return null;
            }

            T? value = root.Deserialize(Info<T>(ProjectJson.ElementReadOptions));
            if (value is null)
            {
                error = "El contenido de un elemento está vacío.";
            }

            return value;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException or FormatException or OverflowException)
        {
            error = "El contenido de un elemento no tiene el formato esperado.";
            return null;
        }
    }

    private static bool RewriteBlobs(JsonNode? node, bool inOptions, IBlobMap? blobs, out string? error)
    {
        error = null;
        switch (node)
        {
            case JsonObject obj:
                foreach (KeyValuePair<string, JsonNode?> property in obj.ToList())
                {
                    if (inOptions && property.Value is JsonValue value && value.TryGetValue(out string? text))
                    {
                        if (text.StartsWith(Effects.OptionDefinition.FilePrefix, StringComparison.Ordinal))
                        {
                            error = "!Un elemento trae una ruta de archivo (file:) donde solo se admiten referencias a blobs.";
                            return false;
                        }

                        if (text.StartsWith(BlobPrefix, StringComparison.Ordinal))
                        {
                            string sha = text[BlobPrefix.Length..];
                            if (!ContentHash.IsValid(sha))
                            {
                                error = "!Un elemento trae una referencia a un blob con una huella que no es SHA-256.";
                                return false;
                            }

                            obj[property.Key] = blobs?.FileReferenceFor(sha) ?? "";
                        }
                    }
                    else if (!RewriteBlobs(property.Value, property.Key == OptionsKey, blobs, out error))
                    {
                        return false;
                    }
                }

                return true;
            case JsonArray array:
                foreach (JsonNode? item in array)
                {
                    if (!RewriteBlobs(item, inOptions: false, blobs, out error))
                    {
                        return false;
                    }
                }

                return true;
            default:
                return true;
        }
    }
}
