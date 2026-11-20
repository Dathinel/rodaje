using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;

namespace Rodaje.Core.Collab;

/// <summary>Límites de lo que se acepta de la red.</summary>
public sealed record WireLimits
{
    public static WireLimits Default { get; } = new();

    /// <summary>Tamaño máximo del parche en bytes UTF-8 (900 KiB: cabe con su envoltorio en el marco de 1 MiB del protocolo).</summary>
    public int MaxPatchBytes { get; init; } = 900 * 1024;

    /// <summary>Profundidad máxima de anidamiento del JSON.</summary>
    public int MaxDepth { get; init; } = 32;

    /// <summary>Cambios de elemento por parche.</summary>
    public int MaxOps { get; init; } = 20_000;

    /// <summary>Longitud máxima de un identificador.</summary>
    public int MaxIdLength { get; init; } = 128;

    /// <summary>Longitud máxima de cualquier texto suelto del JSON (nombres, notas, palabras).</summary>
    public int MaxStringLength { get; init; } = 100_000;
}

/// <summary>
/// Lista de permitidos para lo que llega por la red, antes de abrir o resolver nada: rechaza JSON de más de la profundidad o el
/// tamaño permitidos, campos desconocidos, identificadores repetidos o mal formados, y cualquier ruta (<c>file:</c>, UNC,
/// absoluta, relativa o con <c>..</c>) donde solo cabe una referencia <c>blob:</c>. No lanza nunca.
/// </summary>
public static class WireSanitizer
{
    private const int MaxBlobs = 1_000;
    private const int MaxOrderIds = 200_000;

    private static readonly HashSet<string> PatchKeys = new(["id", "base", "author", "desc", "ops", "orders", "blobs"], StringComparer.Ordinal);
    private static readonly HashSet<string> OpKeys = new(["kind", "id", "parent", "v", "content"], StringComparer.Ordinal);
    private static readonly HashSet<string> OrderKeys = new(["container", "ids"], StringComparer.Ordinal);
    private static readonly HashSet<string> BlobKeys = new(["sha256", "size"], StringComparer.Ordinal);

    /// <summary>Comprueba el texto de un parche; <see langword="null"/> si es aceptable.</summary>
    public static PatchRejection? Check(string json, WireLimits? limits = null)
    {
        limits ??= WireLimits.Default;
        if (json is null)
        {
            return Malformed("No hay parche.");
        }

        if (json.Length > limits.MaxPatchBytes || Encoding.UTF8.GetByteCount(json) > limits.MaxPatchBytes)
        {
            return new PatchRejection(PatchRejectionReason.TooLarge, "El parche es más grande de lo que admite la sala.");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = limits.MaxDepth, AllowDuplicateProperties = false });
            return CheckPatch(document.RootElement, limits);
        }
        catch (JsonException)
        {
            return Malformed("El parche no es un JSON válido, tiene campos repetidos o es demasiado profundo.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException or OverflowException)
        {
            return Malformed("El parche no tiene el formato esperado.");
        }
    }

    internal static PatchRejection Malformed(string message, ElementKey? element = null) =>
        new(PatchRejectionReason.Malformed, message, element);

    private static PatchRejection Hostile(string message, ElementKey? element = null) =>
        new(PatchRejectionReason.Hostile, message, element);

    private static PatchRejection? CheckPatch(JsonElement root, WireLimits limits)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return Malformed("El parche no es un objeto JSON.");
        }

        if (UnknownKey(root, PatchKeys) is { } unknown)
        {
            return Hostile($"El parche trae un campo desconocido («{Shorten(unknown)}»).");
        }

        if (!TryString(root, "id", limits.MaxIdLength, out string? id) || !IsPlainId(id!, limits)
            || !TryString(root, "author", 1_000, out _) || !TryString(root, "desc", 1_000, out _)
            || !root.TryGetProperty("base", out JsonElement baseOrder) || baseOrder.ValueKind != JsonValueKind.Number || !baseOrder.TryGetInt64(out long order) || order < 0)
        {
            return Malformed("Al parche le falta un dato (id, base, author o desc) o no tiene el tipo correcto.");
        }

        if (!root.TryGetProperty("ops", out JsonElement ops) || ops.ValueKind != JsonValueKind.Array)
        {
            return Malformed("Al parche le falta la lista de cambios (ops).");
        }

        if (ops.GetArrayLength() > limits.MaxOps)
        {
            return new PatchRejection(PatchRejectionReason.TooLarge, $"El parche trae más de {limits.MaxOps} cambios.");
        }

        var seen = new HashSet<ElementKey>();
        foreach (JsonElement op in ops.EnumerateArray())
        {
            if (CheckOp(op, limits, seen) is { } problem)
            {
                return problem;
            }
        }

        if (root.TryGetProperty("orders", out JsonElement orders))
        {
            if (orders.ValueKind != JsonValueKind.Array)
            {
                return Malformed("Las listas ordenadas (orders) del parche no son una lista.");
            }

            var containers = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonElement item in orders.EnumerateArray())
            {
                if (CheckOrder(item, limits, containers) is { } problem)
                {
                    return problem;
                }
            }
        }

        if (root.TryGetProperty("blobs", out JsonElement blobs))
        {
            if (blobs.ValueKind != JsonValueKind.Array || blobs.GetArrayLength() > MaxBlobs)
            {
                return Malformed("Los blobs del parche no son una lista o son demasiados.");
            }

            foreach (JsonElement blob in blobs.EnumerateArray())
            {
                if (blob.ValueKind != JsonValueKind.Object || UnknownKey(blob, BlobKeys) is not null
                    || !TryString(blob, "sha256", 64, out string? sha) || !ContentHash.IsValid(sha)
                    || !blob.TryGetProperty("size", out JsonElement size) || size.ValueKind != JsonValueKind.Number || !size.TryGetInt64(out long bytes) || bytes < 0)
                {
                    return Malformed("Un blob del parche no tiene una huella SHA-256 y un tamaño válidos.");
                }
            }
        }

        return null;
    }

    private static PatchRejection? CheckOp(JsonElement op, WireLimits limits, HashSet<ElementKey> seen)
    {
        if (op.ValueKind != JsonValueKind.Object)
        {
            return Malformed("Un cambio del parche no es un objeto.");
        }

        if (UnknownKey(op, OpKeys) is { } unknown)
        {
            return Hostile($"Un cambio del parche trae un campo desconocido («{Shorten(unknown)}»).");
        }

        if (!TryString(op, "kind", 32, out string? wireKind) || !ElementKinds.TryParse(wireKind, out ElementKind kind)
            || !TryString(op, "id", limits.MaxIdLength, out string? id) || !IsPlainId(id!, limits))
        {
            return Malformed("Un cambio del parche tiene una clase o un identificador que no son válidos.");
        }

        var key = new ElementKey(kind, id!);
        if (!seen.Add(key))
        {
            return Malformed($"El parche cambia dos veces el mismo elemento («{Shorten(key.ToString())}»).", key);
        }

        if (!op.TryGetProperty("v", out JsonElement version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int v) || v < 0)
        {
            return Malformed("Un cambio del parche no tiene una versión válida.", key);
        }

        if (op.TryGetProperty("parent", out JsonElement parent) && parent.ValueKind != JsonValueKind.Null
            && (parent.ValueKind != JsonValueKind.String || !IsPlainId(parent.GetString()!, limits)))
        {
            return Malformed("Un cambio del parche tiene un padre que no es un identificador válido.", key);
        }

        bool needsParent = kind is ElementKind.Track or ElementKind.Clip or ElementKind.Marker or ElementKind.Region;
        bool hasParent = op.TryGetProperty("parent", out JsonElement p) && p.ValueKind == JsonValueKind.String;
        if (needsParent != hasParent)
        {
            return Malformed(needsParent ? "Un cambio del parche no dice de qué cuelga." : "Un cambio del parche dice de qué cuelga un elemento que no cuelga de nada.", key);
        }

        if (!op.TryGetProperty("content", out JsonElement content))
        {
            return Malformed("Un cambio del parche no tiene contenido (ni null para borrar).", key);
        }

        if (content.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (content.ValueKind != JsonValueKind.Object)
        {
            return Malformed("El contenido de un cambio del parche no es un objeto.", key);
        }

        return CheckContent(content, kind, key, limits);
    }

    private static PatchRejection? CheckOrder(JsonElement item, WireLimits limits, HashSet<string> containers)
    {
        if (item.ValueKind != JsonValueKind.Object || UnknownKey(item, OrderKeys) is not null
            || !TryString(item, "container", limits.MaxIdLength + 8, out string? container) || !IsContainer(container!, limits)
            || !item.TryGetProperty("ids", out JsonElement ids) || ids.ValueKind != JsonValueKind.Array)
        {
            return Malformed("Una lista ordenada del parche no tiene un contenedor y una lista de identificadores válidos.");
        }

        if (!containers.Add(container!))
        {
            return Malformed("El parche ordena dos veces la misma lista.");
        }

        if (ids.GetArrayLength() > MaxOrderIds)
        {
            return new PatchRejection(PatchRejectionReason.TooLarge, "Una lista ordenada del parche es demasiado larga.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement id in ids.EnumerateArray())
        {
            if (id.ValueKind != JsonValueKind.String || !IsPlainId(id.GetString()!, limits) || !seen.Add(id.GetString()!))
            {
                return Malformed("Una lista ordenada del parche tiene un identificador que no es válido o está repetido.");
            }
        }

        return null;
    }

    private static PatchRejection? CheckContent(JsonElement content, ElementKind kind, ElementKey key, WireLimits limits)
    {
        if (kind == ElementKind.Media && content.TryGetProperty("path", out _))
        {
            return Hostile("Un medio del parche trae una ruta de archivo; los medios viajan sin ruta.", key);
        }

        JsonTypeInfo info = kind switch
        {
            ElementKind.Media => TypeInfo<MediaRef>(),
            ElementKind.Transcript => TypeInfo<MediaTranscript>(),
            ElementKind.Sequence => TypeInfo<Sequence>(),
            ElementKind.Track => TypeInfo<Track>(),
            ElementKind.Clip => TypeInfo<Clip>(),
            ElementKind.Marker => TypeInfo<Marker>(),
            _ => TypeInfo<Region>(),
        };

        string? problem = Walk(content, info, inOptions: false, limits);
        if (problem is not null)
        {
            return problem.StartsWith('!')
                ? Hostile(problem[1..], key)
                : Malformed(problem, key);
        }

        // El identificador de dentro tiene que ser el del cambio: si no, un cambio podría colarse como otro elemento.
        string idProperty = kind == ElementKind.Transcript ? "mediaId" : "id";
        if (!content.TryGetProperty(idProperty, out JsonElement inner) || inner.ValueKind != JsonValueKind.String || inner.GetString() != key.Id)
        {
            return Malformed("El contenido de un cambio no lleva el mismo identificador que el cambio.", key);
        }

        return null;
    }

    private static JsonTypeInfo TypeInfo<T>() => ProjectJson.ElementReadOptions.GetTypeInfo(typeof(T));

    // Devuelve null si está bien; un texto que empieza por «!» es hostil y el resto, mal formado.
    private static string? Walk(JsonElement element, JsonTypeInfo info, bool inOptions, WireLimits limits)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return CheckString(element.GetString()!, inOptions, limits);
            case JsonValueKind.Object:
                break;
            case JsonValueKind.Array:
                if (info.Kind is JsonTypeInfoKind.Enumerable && info.ElementType is { } itemType && ChildInfo(itemType) is { } itemInfo)
                {
                    foreach (JsonElement item in element.EnumerateArray())
                    {
                        if (Walk(item, itemInfo, inOptions: false, limits) is { } problem)
                        {
                            return problem;
                        }
                    }
                }

                return null;
            default:
                return null;
        }

        if (info.Kind is JsonTypeInfoKind.Dictionary && info.ElementType is { } valueType)
        {
            JsonTypeInfo? valueInfo = ChildInfo(valueType);
            if (valueInfo is null)
            {
                return null;
            }

            foreach (JsonProperty entry in element.EnumerateObject())
            {
                if (entry.Name.Length > limits.MaxStringLength)
                {
                    return "Un nombre del contenido es demasiado largo.";
                }

                if (Walk(entry.Value, valueInfo, inOptions, limits) is { } problem)
                {
                    return problem;
                }
            }

            return null;
        }

        if (info.Kind is JsonTypeInfoKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                JsonPropertyInfo? known = info.Properties.FirstOrDefault(p => string.Equals(p.Name, property.Name, StringComparison.Ordinal));
                if (known is null)
                {
                    return $"!El contenido trae un campo desconocido («{Shorten(property.Name)}»).";
                }

                JsonTypeInfo? child = ChildInfo(known.PropertyType);
                if (child is not null && Walk(property.Value, child, inOptions: string.Equals(property.Name, "options", StringComparison.Ordinal), limits) is { } problem)
                {
                    return problem;
                }
            }
        }

        // Los tipos con convertidor propio (tiempos, fracciones, palabras) no se miran por dentro: el lector los valida.
        return null;
    }

    private static string? CheckString(string text, bool inOptions, WireLimits limits)
    {
        if (text.Length > limits.MaxStringLength)
        {
            return "Un texto del contenido es demasiado largo.";
        }

        if (!inOptions)
        {
            return null;
        }

        if (text.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            return "!El contenido trae una ruta de archivo (file:) donde solo se admiten referencias a blobs.";
        }

        if (text.StartsWith("\\\\", StringComparison.Ordinal) || text.StartsWith("//", StringComparison.Ordinal)
            || (text.Length >= 3 && char.IsAsciiLetter(text[0]) && text[1] == ':' && text[2] is '\\' or '/'))
        {
            return "!El contenido trae una ruta de red o absoluta donde no se admite.";
        }

        if (text.StartsWith(ElementCodec.BlobPrefix, StringComparison.Ordinal) && !ContentHash.IsValid(text[ElementCodec.BlobPrefix.Length..]))
        {
            return "!El contenido trae una referencia a un blob con una huella que no es SHA-256.";
        }

        return null;
    }

    private static bool IsContainer(string container, WireLimits limits)
    {
        if (container is PatchContainers.Media or PatchContainers.Sequences or PatchContainers.Transcripts)
        {
            return true;
        }

        int colon = container.IndexOf(':', StringComparison.Ordinal);
        return colon > 0 && container[..colon] is "vtracks" or "atracks" or "markers" or "regions"
            && IsPlainId(container[(colon + 1)..], limits);
    }

    // Identificador de la red: no vacío, de longitud acotada, sin caracteres de control (el validador del proyecto exige lo mismo).
    private static bool IsPlainId(string id, WireLimits limits) =>
        id.Length > 0 && id.Length <= limits.MaxIdLength && !id.Any(char.IsControl) && !string.IsNullOrWhiteSpace(id);

    private static bool TryString(JsonElement owner, string name, int maxLength, out string? value)
    {
        value = null;
        if (!owner.TryGetProperty(name, out JsonElement property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString();
        return value is not null && value.Length <= maxLength;
    }

    private static string? UnknownKey(JsonElement owner, HashSet<string> allowed)
    {
        foreach (JsonProperty property in owner.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
            {
                return property.Name;
            }
        }

        return null;
    }

    private static string Shorten(string text)
    {
        string clean = new([.. text.Take(40).Select(c => char.IsControl(c) ? '?' : c)]);
        return text.Length <= 40 ? clean : clean + "…";
    }

    private static JsonTypeInfo? ChildInfo(Type type)
    {
        try
        {
            return ProjectJson.ElementReadOptions.GetTypeInfo(type);
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }
}
