using System.Text.Json;
using System.Text.Json.Serialization;
using FotoToma.Core.Timing;

namespace FotoToma.Core.Serialization;

/// <summary>
/// Escribe un <see cref="Time"/> como un entero JSON de ticks (1 s = 705 600 000 ticks).
/// </summary>
/// <remarks>
/// Un entero y no segundos con decimales para que el archivo guarde el valor exacto. Los enteros de
/// 64 bits caben sin pérdida en lectores como Python; en JavaScript hay que leerlos con BigInt si
/// pasan de 2^53, que equivale a unos 148 días, así que en la práctica un número normal basta.
/// </remarks>
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
