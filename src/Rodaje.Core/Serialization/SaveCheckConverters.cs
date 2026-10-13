using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Rodaje.Core.Model;

namespace Rodaje.Core.Serialization;

/// <summary>
/// Al guardar, un número que JSON no puede representar (NaN o infinito) se rechaza con un error que dice dónde está,
/// en vez de la <see cref="ArgumentException"/> en inglés del escritor.
/// </summary>
/// <remarks>Solo se usa al escribir: el lector ya rechaza esos valores porque no son JSON.</remarks>
internal sealed class FiniteDoubleConverter(bool locate = false) : JsonConverter<double>
{
    public const string Problem = "no es un número finito (NaN o infinito)";

    public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetDouble();

    public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (double.IsFinite(value))
        {
            writer.WriteNumberValue(value);
        }
        else if (locate)
        {
            writer.WriteStringValue(SaveCheck.Marker);
        }
        else
        {
            throw new JsonException(Problem);
        }
    }
}

/// <summary>
/// Al guardar, un texto con un sustituto UTF-16 suelto (medio carácter) se rechaza: el escritor lo cambiaría en
/// silencio por U+FFFD, y dos claves distintas podían quedar iguales y dejar un archivo que no se abre.
/// </summary>
/// <remarks>Cubre también las claves de los diccionarios (parámetros y opciones de los efectos).</remarks>
internal sealed class WellFormedStringConverter(bool locate = false) : JsonConverter<string>
{
    public const string Problem = "tiene un carácter incompleto (un sustituto UTF-16 suelto) que no se puede guardar en UTF-8";

    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetString();

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(Check(value));
    }

    public override string ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.GetString()!;

    public override void WriteAsPropertyName(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WritePropertyName(Check(value));
    }

    /// <summary>Si el texto tiene un sustituto alto sin su bajo detrás, o un bajo sin su alto delante.</summary>
    public static bool HasLoneSurrogate(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                i++;
            }
            else if (char.IsSurrogate(c))
            {
                return true;
            }
        }

        return false;
    }

    private string Check(string value) =>
        !HasLoneSurrogate(value) ? value
        : locate ? SaveCheck.Marker
        : throw new JsonException(Problem);
}

/// <summary>Encuentra la ruta JSON del primer valor que no se puede guardar, para el mensaje de error.</summary>
/// <remarks>
/// La ruta del <see cref="JsonException"/> al escribir no lleva índices ni los nombres del archivo, así que se repite el
/// guardado con convertidores que en lugar de fallar escriben <see cref="Marker"/> y se busca la marca.
/// </remarks>
internal static class SaveCheck
{
    /// <summary>Texto que ningún proyecto tiene: empieza por un no-carácter de Unicode.</summary>
    public const string Marker = "\uFDD0rodaje: valor que no se puede guardar";

    public static string? Locate(Project project)
    {
        try
        {
            return Locate(JsonSerializer.SerializeToNode(project, ProjectJson.LocateInfo));
        }
        catch (ArgumentException)
        {
            // Dos claves que solo se distinguían por el carácter incompleto quedan iguales con la marca.
            return null;
        }
    }

    private static string? Locate(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach ((string name, JsonNode? child) in obj)
                {
                    if (name == Marker)
                    {
                        return $"{obj.GetPath()} (el nombre de una clave)";
                    }

                    if (Locate(child) is { } found)
                    {
                        return found;
                    }
                }

                return null;
            case JsonArray array:
                foreach (JsonNode? item in array)
                {
                    if (Locate(item) is { } found)
                    {
                        return found;
                    }
                }

                return null;
            case JsonValue value when value.TryGetValue(out string? text) && text == Marker:
                return value.GetPath();
            default:
                return null;
        }
    }
}
