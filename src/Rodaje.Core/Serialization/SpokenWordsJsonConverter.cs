using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Rodaje.Core.Model;

namespace Rodaje.Core.Serialization;

/// <summary>
/// Las palabras de una transcripción se escriben en una sola línea: son casi todo el peso del archivo
/// (una hora de habla son unas 9 000 y ~0,5 MB así; una por línea pasarían del megabyte, D-061).
/// Al leer da igual cómo estaban escritas. Las comprobaciones de valores (número finito, texto bien formado,
/// <see cref="TimeJsonConverter"/>) se conservan porque cada palabra sigue pasando por sus metadatos generados.
/// </summary>
internal sealed class SpokenWordsJsonConverter : JsonConverter<List<SpokenWord>>
{
    public override List<SpokenWord> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("Las palabras de una transcripción deben ser una lista.");
        }

        var info = (JsonTypeInfo<SpokenWord>)options.GetTypeInfo(typeof(SpokenWord));
        var words = new List<SpokenWord>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
            {
                return words;
            }

            words.Add(JsonSerializer.Deserialize(ref reader, info)!);
        }

        throw new JsonException();
    }

    public override void Write(Utf8JsonWriter writer, List<SpokenWord> value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        var info = (JsonTypeInfo<SpokenWord>)options.GetTypeInfo(typeof(SpokenWord));
        var buffer = new ArrayBufferWriter<byte>();
        using (var compact = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false, Encoder = options.Encoder }))
        {
            compact.WriteStartArray();
            foreach (SpokenWord word in value)
            {
                JsonSerializer.Serialize(compact, word, info);
            }

            compact.WriteEndArray();
        }

        writer.WriteRawValue(buffer.WrittenSpan, skipInputValidation: true);
    }
}
