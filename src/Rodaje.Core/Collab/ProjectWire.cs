using System.Text;
using System.Text.Json;
using Rodaje.Core.Model;

namespace Rodaje.Core.Collab;

/// <summary>Resultado de leer la copia inicial de una sala: el proyecto y sus versiones, o por qué se rechaza.</summary>
public sealed record ProjectParseResult(Project? Project, ElementVersions? Versions, PatchRejection? Rejection)
{
    public bool IsValid => Project is not null;
}

/// <summary>
/// La copia inicial que el anfitrión da a quien entra: todo lo compartido del proyecto en su forma de red (sin rutas de medios,
/// sin nombre, con las referencias <c>file:</c> como blobs) y las versiones de cada elemento. Es un parche de altas sobre un
/// proyecto vacío, así que pasa por el mismo saneador y las mismas comprobaciones que cualquier parche.
/// </summary>
public static class ProjectWire
{
    /// <summary>Tamaño máximo del texto de una copia inicial (64 MiB, el límite del diseño).</summary>
    public const int MaxBytes = 64 << 20;

    private static WireLimits InitialLimits { get; } = new() { MaxPatchBytes = MaxBytes, MaxOps = 1_000_000 };

    /// <summary>
    /// El JSON de la copia inicial de <paramref name="project"/>. <paramref name="versions"/> (las del anfitrión) viajan con ella; sin
    /// ellas, todo empieza en la versión 0.
    /// </summary>
    /// <exception cref="Rodaje.Core.Serialization.ProjectFormatException">Si el proyecto tiene un valor que el archivo no puede guardar.</exception>
    public static string ToJson(Project project, ElementVersions? versions = null, IBlobMap? blobs = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        SharedState full = SharedState.Capture(project, blobs);
        var empty = new SharedState(project.Id, new Dictionary<ElementKey, ElementState>(), new Dictionary<string, IReadOnlyList<string>>());
        Patch patch = PatchDiff.Create(empty, full, new ElementVersions(), new PatchHeader("inicial", 0, "anfitrion", "Proyecto inicial"))
            ?? new Patch("inicial", 0, "anfitrion", "Proyecto inicial", [], [], []);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteString("project", project.Id);
            writer.WritePropertyName("versions");
            writer.WriteRawValue((versions ?? new ElementVersions()).ToJson());
            writer.WritePropertyName("patch");
            writer.WriteRawValue(PatchWire.ToJson(patch));
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
    }

    /// <summary>
    /// Lee la copia inicial que llegó del anfitrión y arma el proyecto (sin nombre y con los medios sin ruta). Nunca lanza: un
    /// texto hostil, demasiado grande o que dejaría el proyecto inválido devuelve el rechazo.
    /// </summary>
    public static ProjectParseResult TryParse(string json, IBlobMap? blobs = null)
    {
        if (json is null || Encoding.UTF8.GetByteCount(json) > MaxBytes)
        {
            return Rejected(PatchRejectionReason.TooLarge, "La copia inicial del proyecto es más grande de lo que admite la sala.");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = InitialLimits.MaxDepth + 2, AllowDuplicateProperties = false });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Any(p => p.Name is not ("project" or "versions" or "patch"))
                || !root.TryGetProperty("project", out JsonElement id) || id.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("versions", out JsonElement versionsJson) || versionsJson.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("patch", out JsonElement patchJson))
            {
                return Rejected(PatchRejectionReason.Malformed, "La copia inicial del proyecto no tiene el formato esperado.");
            }

            string projectId = id.GetString()!;
            if (projectId.Length is 0 or > 128 || projectId.Any(char.IsControl) || string.IsNullOrWhiteSpace(projectId))
            {
                return Rejected(PatchRejectionReason.Malformed, "La copia inicial trae un identificador de proyecto que no es válido.");
            }

            ElementVersions? versions = ElementVersions.TryParse(versionsJson.GetRawText());
            if (versions is null)
            {
                return Rejected(PatchRejectionReason.Malformed, "La copia inicial trae versiones que no son válidas.");
            }

            PatchParseResult parsed = PatchWire.TryParse(patchJson.GetRawText(), InitialLimits);
            if (parsed.Patch is null)
            {
                return new ProjectParseResult(null, null, parsed.Rejection);
            }

            var project = new Project { Id = projectId };
            PatchResult applied = PatchApply.TryApply(project, new ElementVersions(), parsed.Patch, new PatchApplyOptions { Limits = InitialLimits, Blobs = blobs });
            return applied.IsAccepted
                ? new ProjectParseResult(project, versions, null)
                : new ProjectParseResult(null, null, applied.Rejection);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException or FormatException)
        {
            return Rejected(PatchRejectionReason.Malformed, "La copia inicial del proyecto no es un JSON válido.");
        }
    }

    private static ProjectParseResult Rejected(PatchRejectionReason reason, string message) =>
        new(null, null, new PatchRejection(reason, message));
}
