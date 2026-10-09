using System.Text.Json;
using System.Text.Json.Serialization;
using FotoToma.Core.Timing;

namespace FotoToma.Core.Serialization;

/// <summary>
/// Escribe un <see cref="Rational"/> como <c>{"num":30000,"den":1001}</c>.
/// </summary>
/// <remarks>
/// Al leer, los dos campos son obligatorios, enteros y positivos; la fracción se normaliza, así que
/// <c>{"num":60,"den":2}</c> se lee como 30/1. Los campos desconocidos dentro del objeto se ignoran.
/// </remarks>
public sealed class RationalJsonConverter : JsonConverter<Rational>
{
    private const string InvalidMessage =
        "Una tasa debe ser un objeto {\"num\": entero, \"den\": entero} con los dos números positivos.";

    public override Rational Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException(InvalidMessage);
        }

        long? num = null;
        long? den = null;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            // Dentro de un objeto, el lector siempre entrega un nombre de propiedad seguido de su valor.
            string? name = reader.GetString();
            reader.Read();

            if (name is "num" or "den")
            {
                if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt64(out long value))
                {
                    throw new JsonException(InvalidMessage);
                }

                if (name == "num")
                {
                    num = value;
                }
                else
                {
                    den = value;
                }
            }
            else
            {
                reader.Skip();
            }
        }

        if (num is not { } n || den is not { } d || n <= 0 || d <= 0)
        {
            throw new JsonException(InvalidMessage);
        }

        return new Rational(n, d);
    }

    public override void Write(Utf8JsonWriter writer, Rational value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStartObject();
        writer.WriteNumber("num", value.Num);
        writer.WriteNumber("den", value.Den);
        writer.WriteEndObject();
    }
}
