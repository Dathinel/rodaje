using System.Text.Json;
using System.Text.Json.Serialization;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Serialization;

/// <summary>Escribe un <see cref="Time"/> como entero de ticks, para guardar el valor exacto.</summary>
public sealed class TimeJsonConverter : JsonConverter<Time>
{
    public override Time Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt64(out long ticks))
        {
            throw new JsonException("Un tiempo debe ser un número entero de ticks (705 600 000 ticks por segundo).");
        }

        return Time.FromTicks(ticks);
    }

    public override void Write(Utf8JsonWriter writer, Time value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteNumberValue(value.Ticks);
    }
}
