using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Serialization;

/// <summary>Genera el esquema JSON (draft 2020-12) de <c>.rodaje</c> desde los mismos metadatos que usa el lector.</summary>
/// <remarks>
/// Describe la forma, no las invariantes (<see cref="ModelValidator"/>). Una prueba verifica que
/// <c>schema/rodaje-proyecto.v7.schema.json</c> coincide con <see cref="Export"/>.
/// </remarks>
public static class ProjectSchema
{
    public const string SchemaId = "https://github.com/Dathinel/rodaje/schema/rodaje-proyecto.v7.schema.json";

    /// <summary>Esquema como JSON indentado con saltos de línea LF.</summary>
    public static string Export()
    {
        var exporterOptions = new JsonSchemaExporterOptions
        {
            TreatNullObliviousAsNonNullable = true,
            TransformSchemaNode = Transform,
        };

        JsonNode schema = ProjectJson.ProjectInfo.GetJsonSchemaAsNode(exporterOptions);

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Indented = true,
            NewLine = "\n",
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            schema.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static JsonNode Transform(JsonSchemaExporterContext context, JsonNode schema)
    {
        Type type = context.TypeInfo.Type;

        // Los convertidores propios no describen su forma; se completa a mano.
        if (type == typeof(Time) || type == typeof(Time?))
        {
            schema = new JsonObject
            {
                ["type"] = type == typeof(Time) ? "integer" : new JsonArray("integer", "null"),
                ["description"] = "Tiempo exacto en ticks: 705 600 000 ticks por segundo.",
            };
        }
        else if (context.PropertyInfo?.DeclaringType == typeof(SpeedPoint))
        {
            bool at = context.PropertyInfo.Name == "at";
            schema["minimum"] = at ? 0 : RetimeMath.MinHundredths;
            schema["maximum"] = at ? SpeedCurve.AtScale : RetimeMath.MaxHundredths;
        }
        else if (type == typeof(Rational) || type == typeof(Rational?))
        {
            var rational = new JsonObject
            {
                ["type"] = "object",
                ["description"] = "Fracción positiva, por ejemplo 30000/1001 para 29,97 cuadros por segundo.",
                ["properties"] = new JsonObject
                {
                    ["num"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1 },
                    ["den"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1 },
                },
                ["required"] = new JsonArray("num", "den"),
            };
            schema = type == typeof(Rational)
                ? rational
                : new JsonObject { ["anyOf"] = new JsonArray(rational, new JsonObject { ["type"] = "null" }) };
        }

        if (context.Path.IsEmpty && type == typeof(Project))
        {
            var root = new JsonObject
            {
                ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
                ["$id"] = SchemaId,
                ["title"] = "Proyecto de Rodaje",
                ["description"] =
                    $"Archivo {ProjectSerializer.FileExtension} versión {Project.CurrentSchemaVersion}. " +
                    "Las reglas que el esquema no expresa están en docs/formato-proyecto.md.",
            };
            foreach ((string key, JsonNode? value) in schema.AsObject().ToList())
            {
                schema.AsObject().Remove(key);
                root[key] = value;
            }

            root["properties"]!["schemaVersion"] = new JsonObject
            {
                ["type"] = "integer",
                ["const"] = Project.CurrentSchemaVersion,
            };
            schema = root;
        }

        return schema;
    }
}
