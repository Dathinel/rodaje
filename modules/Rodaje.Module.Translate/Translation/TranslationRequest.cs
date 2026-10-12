using System.Text.Json;
using System.Text.Json.Serialization;
using Rodaje.Modules.Protocol;

namespace Rodaje.Module.Translate.Translation;

/// <summary>Un subtítulo que entra: su id (el del clip en la app) y su texto, que puede traer saltos de línea.</summary>
public sealed record SourceSegment(string Id, string Text);

/// <summary>Un subtítulo traducido: el mismo id y el texto ya partido en líneas con <c>\n</c>.</summary>
public sealed record TranslatedSegment(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("text")] string Text);

/// <summary>El resultado de <c>traducir</c> (<c>docs/fase-7.md</c>): los mismos ids, en el mismo orden.</summary>
public sealed record TranslationResult(
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("target")] string Target,
    [property: JsonPropertyName("device")] string Device,
    [property: JsonPropertyName("segments")] IReadOnlyList<TranslatedSegment> Segments,
    [property: JsonPropertyName("untranslated")] IReadOnlyList<string> Untranslated);

[JsonSerializable(typeof(TranslationResult))]
internal sealed partial class TranslationJson : JsonSerializerContext;

/// <summary>
/// Los parámetros de <c>traducir</c>, comprobados: el módulo desconfía del editor igual que el editor del módulo, así
/// que cada campo tiene su límite y lo inválido se rechaza con <see cref="ErrorCodes.InvalidParameters"/>.
/// </summary>
internal sealed record TranslationRequest(
    TranslationLanguage? Source,
    TranslationLanguage Target,
    IReadOnlyList<SourceSegment> Segments,
    int MaxLineChars,
    string Device,
    string? Model,
    int Threads,
    int GpuLayers,
    int BatchSize)
{
    public const int MaxSegments = 5000;
    public const int MaxIdLength = 64;

    /// <summary>Un subtítulo largo tiene unos 84 caracteres; 500 deja margen para textos de títulos.</summary>
    public const int MaxTextLength = 500;

    public const int DefaultLineChars = 42;
    public const int DefaultBatchSize = 8;

    /// <summary>
    /// Lee los parámetros. Los subtítulos van en <c>segments</c> o, si no caben en un mensaje, en el JSON
    /// <c>{"segments":[…]}</c> de <paramref name="input"/> (el primer búfer de bytes).
    /// </summary>
    public static TranslationRequest Parse(JsonElement? parameters, ReadOnlySpan<byte> input = default)
    {
        JsonElement p = parameters is { ValueKind: JsonValueKind.Object } o ? o : default;
        bool has = p.ValueKind == JsonValueKind.Object;

        string? targetCode = has ? Text(p, "target") : null;
        TranslationLanguage target = Languages.Find(targetCode)
            ?? throw Invalid(targetCode is null ? "Falta «target», el idioma al que se traduce." : $"No sé traducir al idioma «{targetCode}».");

        string sourceCode = (has ? Text(p, "source") : null) ?? "auto";
        TranslationLanguage? source = sourceCode.Equals("auto", StringComparison.OrdinalIgnoreCase)
            ? null
            : Languages.Find(sourceCode) ?? throw Invalid($"No conozco el idioma de origen «{sourceCode}».");
        if (source is not null && source.Code == target.Code)
        {
            throw Invalid("El idioma de origen y el de destino son el mismo.");
        }

        IReadOnlyList<SourceSegment> segments;
        if (has && p.TryGetProperty("segments", out JsonElement inline))
        {
            if (!input.IsEmpty)
            {
                throw Invalid("Los subtítulos van en «segments» o en el búfer de entrada, no en los dos.");
            }

            segments = ReadSegments(inline);
        }
        else if (!input.IsEmpty)
        {
            segments = FromBuffer(input);
        }
        else
        {
            throw Invalid("Faltan los subtítulos («segments»).");
        }

        string device = (has ? Text(p, "device") : null) ?? "auto";
        if (device is not ("auto" or "gpu" or "cpu"))
        {
            throw Invalid($"«device» es auto, gpu o cpu, no «{device}».");
        }

        int maxLineChars = Int(p, has, "maxLineChars") is { } m ? m : DefaultLineChars;
        if (maxLineChars is < 10 or > 200)
        {
            throw Invalid("«maxLineChars» va de 10 a 200.");
        }

        return new TranslationRequest(
            source,
            target,
            segments,
            maxLineChars,
            device,
            has ? Text(p, "model") : null,
            Math.Clamp(Int(p, has, "threads") ?? 0, 0, 64),
            Math.Clamp(Int(p, has, "gpuLayers") ?? -1, -1, 999),
            Math.Clamp(Int(p, has, "batch") ?? DefaultBatchSize, 1, 32));
    }

    private static List<SourceSegment> FromBuffer(ReadOnlySpan<byte> input)
    {
        // El editor puede pasar un búfer más grande que el JSON: lo que sobra son ceros.
        int end = input.IndexOf((byte)0);
        ReadOnlySpan<byte> json = end >= 0 ? input[..end] : input;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions { MaxDepth = 8 });
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("segments", out JsonElement s))
            {
                throw Invalid("El búfer de entrada no trae «segments».");
            }

            return ReadSegments(s);
        }
        catch (JsonException)
        {
            throw Invalid("El búfer de entrada no es JSON válido.");
        }
    }

    private static List<SourceSegment> ReadSegments(JsonElement array)
    {
        if (array.ValueKind != JsonValueKind.Array)
        {
            throw Invalid("«segments» tiene que ser una lista.");
        }

        int count = array.GetArrayLength();
        if (count == 0)
        {
            throw Invalid("No hay subtítulos que traducir.");
        }

        if (count > MaxSegments)
        {
            throw Invalid($"Son {count} subtítulos; el máximo por llamada es {MaxSegments}.");
        }

        List<SourceSegment> list = new(count);
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (JsonElement e in array.EnumerateArray())
        {
            string? id = e.ValueKind == JsonValueKind.Object ? Text(e, "id") : null;
            string? text = e.ValueKind == JsonValueKind.Object ? Text(e, "text") : null;
            if (id is not { Length: > 0 and <= MaxIdLength } || id.Any(char.IsControl))
            {
                throw Invalid("Cada subtítulo necesita un «id» de 1 a 64 caracteres.");
            }

            if (text is null || text.Length > MaxTextLength)
            {
                throw Invalid($"El subtítulo «{id}» necesita un «text» de hasta {MaxTextLength} caracteres.");
            }

            if (!seen.Add(id))
            {
                throw Invalid($"El id «{id}» está repetido.");
            }

            list.Add(new SourceSegment(id, text));
        }

        return list;
    }

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement e, bool has, string name) =>
        has && e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : null;

    private static ModuleCallException Invalid(string message) => new(ErrorCodes.InvalidParameters, message);
}
