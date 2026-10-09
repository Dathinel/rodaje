using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using FotoToma.Core.Model;
using FotoToma.Core.Timing;

namespace FotoToma.Core.Serialization;

/// <summary>
/// Genera el esquema JSON (draft 2020-12) del formato <c>.ftoma</c> a partir de los mismos metadatos que
/// usa el lector, de modo que el esquema publicado no se puede desincronizar del código.
/// </summary>
/// <remarks>
/// El esquema describe la forma del archivo, no todas sus reglas: que los clips de una pista no se
/// solapen o que los identificadores no se repitan lo comprueba <see cref="ModelValidator"/> y está
/// explicado en <c>docs/formato-proyecto.md</c>. La copia versionada vive en
/// <c>schema/fototoma-proyecto.v1.schema.json</c>; una prueba verifica que coincide con este método.
/// </remarks>
public static class ProjectSchema
{
    /// <summary>Identificador estable del esquema de la versión actual.</summary>
    public const string SchemaId = "https://github.com/Dathinel/fototoma/schema/fototoma-proyecto.v1.schema.json";

    /// <summary>Devuelve el esquema como texto JSON indentado con saltos de línea LF.</summary>
    public static string Export()
    {
        var exporterOptions = new JsonSchemaExporterOptions
        {
            TreatNullObliviousAsNonNullable = true,
            TransformSchemaNode = Transform,
        };

        JsonNode schema = ProjectJsonContext.ProjectInfo.GetJsonSchemaAsNode(exporterOptions);

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
        if (type == typeof(Time))
        {
            schema = new JsonObject
            {
                ["type"] = "integer",
                ["description"] = "Tiempo exacto en ticks: 705 600 000 ticks por segundo.",
            };
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
                ["title"] = "Proyecto de FotoToma",
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
