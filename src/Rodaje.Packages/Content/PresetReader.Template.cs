using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Rodaje.Packages.Content;

internal static partial class PresetReader
{
    public static TemplateDocument ReadTemplate(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[8192];
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (buffer.Length + read > TemplateLimits.MaxFileBytes)
            {
                throw Fail("El archivo de rodaje.template pesa más de 256 KB.");
            }

            buffer.Write(chunk, 0, read);
        }

        buffer.Position = 0;
        using JsonDocument parsed = Parse(buffer, TemplateDocument.Format);
        JsonElement root = parsed.RootElement;
        Header(root, TemplateDocument.Format, TemplateDocument.Version);
        CheckTemplateProperties(root);
        (string id, string name) = Identity(root);
        TemplateKind kind = String(root, "kind") switch
        {
            "title" => TemplateKind.Title,
            "transition" => TemplateKind.Transition,
            "effect" => TemplateKind.Effect,
            _ => throw Fail("kind tiene que ser title, transition o effect."),
        };
        (int width, int height) = Reference(root);
        double duration = Duration(root, TemplateLimits.MaxSeconds);
        double minimum = Number(root, "minSeconds", null, 0, duration, "minSeconds");
        if (!root.TryGetProperty("body", out JsonElement b) || b.ValueKind != JsonValueKind.Object)
        {
            throw Fail("body tiene que ser un objeto.");
        }

        var body = (JsonObject)JsonNode.Parse(b.GetRawText(), documentOptions: DocumentOptions)!;
        _ = ValidateTemplateBody(kind, body);
        IReadOnlyList<TemplateParameter> parameters = ReadTemplateParameters(root, body);
        return new TemplateDocument(id, name, kind, width, height, duration, minimum, body, parameters);
    }

    // JsonNode materializa los objetos de forma diferida: se rechazan duplicados antes de recorrer rutas.
    private static void CheckTemplateProperties(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty p in node.EnumerateObject())
            {
                if (!names.Add(p.Name))
                {
                    throw Fail($"La propiedad «{Show(p.Name)}» está repetida.");
                }

                CheckTemplateProperties(p.Value);
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement child in node.EnumerateArray())
            {
                CheckTemplateProperties(child);
            }
        }
    }

    private static IReadOnlyList<TemplateParameter> ReadTemplateParameters(JsonElement root, JsonObject body)
    {
        if (!root.TryGetProperty("parameters", out JsonElement list))
        {
            return Array.Empty<TemplateParameter>();
        }

        if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() > TemplateLimits.MaxParameters)
        {
            throw Fail("parameters tiene que ser una lista de hasta 32 parámetros.");
        }

        var result = new List<TemplateParameter>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement p in list.EnumerateArray())
        {
            if (p.ValueKind != JsonValueKind.Object)
            {
                throw Fail("Cada parámetro tiene que ser un objeto.");
            }

            string id = TemplateIdentifier(p, ids, "parameters");
            string label = TemplateLabel(p, "label");
            TemplateParameterType type = String(p, "type") switch
            {
                "text" => TemplateParameterType.Text,
                "color" => TemplateParameterType.Color,
                "font" => TemplateParameterType.Font,
                "number" => TemplateParameterType.Number,
                "bool" => TemplateParameterType.Bool,
                "choice" => TemplateParameterType.Choice,
                _ => throw Fail($"El parámetro «{Show(id)}» tiene un type desconocido."),
            };
            double? min = null, max = null, step = null;
            if (type == TemplateParameterType.Number)
            {
                min = Number(p, "min", null, double.MinValue, double.MaxValue, "min");
                max = Number(p, "max", null, min.Value, double.MaxValue, "max");
                if (p.TryGetProperty("step", out _))
                {
                    step = Number(p, "step", null, double.Epsilon, double.MaxValue, "step");
                }
            }

            var options = new List<TemplateOption>();
            if (type == TemplateParameterType.Choice)
            {
                if (!p.TryGetProperty("options", out JsonElement opts) || opts.ValueKind != JsonValueKind.Array
                    || opts.GetArrayLength() is < 1 or > TemplateLimits.MaxOptions)
                {
                    throw Fail("choice necesita de 1 a 16 options.");
                }

                foreach (JsonElement opt in opts.EnumerateArray())
                {
                    if (opt.ValueKind != JsonValueKind.Object || !opt.TryGetProperty("value", out JsonElement value)
                        || value.ValueKind == JsonValueKind.Null)
                    {
                        throw Fail("Cada opción necesita label y value no nulo.");
                    }

                    JsonNode optionValue = JsonNode.Parse(value.GetRawText(), documentOptions: DocumentOptions)!;
                    if (options.Count > 0 && TemplateValueKind(optionValue) != TemplateValueKind(options[0].StoredValue))
                    {
                        throw Fail("Los value de options tienen que tener el mismo tipo.");
                    }

                    options.Add(new TemplateOption(TemplateLabel(opt, "label"), optionValue));
                }
            }

            if (!p.TryGetProperty("targets", out JsonElement targets) || targets.ValueKind != JsonValueKind.Array
                || targets.GetArrayLength() is < 1 or > TemplateLimits.MaxTargets)
            {
                throw Fail("targets necesita de 1 a 16 rutas sobre body.");
            }

            var paths = new List<string>();
            foreach (JsonElement target in targets.EnumerateArray())
            {
                if (target.ValueKind != JsonValueKind.String)
                {
                    throw Fail("Cada ruta de targets tiene que ser un texto.");
                }

                paths.Add(target.GetString()!);
            }

            var parameter = new TemplateParameter(id, label, type, paths.AsReadOnly(), min, max, step, options.AsReadOnly());
            foreach (string path in paths)
            {
                (JsonNode parent, string segment) = ResolveTemplateTarget(body, path);
                ValidateTemplateValue(parameter, GetTemplateChild(parent, segment, path), supplied: false);
            }

            result.Add(parameter);
        }

        return result.AsReadOnly();
    }

    private static string TemplateLabel(JsonElement owner, string property)
    {
        string label = String(owner, property) is { } raw ? SafeText.Clean(raw) : "";
        return label.Length is > 0 and <= SafeText.MaxName ? label : throw Fail($"{property} necesita un texto de 1 a 80 caracteres.");
    }

    private static string TemplateIdentifier(JsonElement owner, HashSet<string> ids, string where)
        => TemplateIdentifier(String(owner, "id"), ids, where);

    private static string TemplateIdentifier(string? raw, HashSet<string> ids, string where)
    {
        string id = raw ?? "";
        if (!Identifiers.IsValid(id) || !ids.Add(id))
        {
            throw Fail($"{where}: id «{Show(id)}» no es válido o está repetido.");
        }

        return id;
    }

    internal static (JsonNode Parent, string Segment) ResolveTemplateTarget(JsonObject body, string path)
    {
        string[] segments = path.Split('/');
        if (segments.Any(string.IsNullOrEmpty))
        {
            throw Fail($"La ruta «{Show(path)}» no existe en body.");
        }

        JsonNode current = body;
        for (int i = 0; i < segments.Length - 1; i++)
        {
            current = GetTemplateChild(current, segments[i], path)
                ?? throw Fail($"La ruta «{Show(path)}» no existe en body.");
            if (segments[i] == "layers" && current is JsonArray layers)
            {
                i++;
                string id = segments[i];
                current = layers.OfType<JsonObject>().FirstOrDefault(l => NodeString(l["id"]) == id)
                    ?? throw Fail($"La ruta «{Show(path)}» no encuentra la capa «{Show(id)}».");
                if (i == segments.Length - 1)
                {
                    throw Fail("Un destino tiene que señalar un campo de la capa.");
                }
            }
        }

        _ = GetTemplateChild(current, segments[^1], path);
        return (current, segments[^1]);
    }

    private static JsonNode? GetTemplateChild(JsonNode parent, string segment, string path)
    {
        if (parent is JsonObject obj && obj.TryGetPropertyValue(segment, out JsonNode? child))
        {
            return child;
        }

        if (parent is JsonArray list && int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out int index)
            && index >= 0 && index < list.Count)
        {
            return list[index];
        }

        throw Fail($"La ruta «{Show(path)}» no existe en body.");
    }

    private static JsonValueKind TemplateValueKind(JsonNode? node) => node?.GetValueKind() switch
    {
        JsonValueKind.False => JsonValueKind.True,
        { } kind => kind,
        _ => JsonValueKind.Null,
    };

    private static string? NodeString(JsonNode? node) =>
        node?.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : null;

    internal static void ValidateTemplateValue(TemplateParameter p, JsonNode? value, bool supplied)
    {
        JsonValueKind kind = TemplateValueKind(value);
        bool valid = p.Type switch
        {
            TemplateParameterType.Text => kind == JsonValueKind.String,
            TemplateParameterType.Color => NodeString(value) is { } color && color.Length is 7 or 9
                && color[0] == '#' && !color.AsSpan(1).ContainsAnyExcept(HexDigits),
            TemplateParameterType.Font => kind == JsonValueKind.Object,
            TemplateParameterType.Number => kind == JsonValueKind.Number
                && double.TryParse(value!.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double n)
                && double.IsFinite(n) && n >= p.Min && n <= p.Max,
            TemplateParameterType.Bool => kind == JsonValueKind.True,
            TemplateParameterType.Choice => kind == TemplateValueKind(p.Options[0].StoredValue)
                && (!supplied || p.Options.Any(o => JsonNode.DeepEquals(o.StoredValue, value))),
            _ => false,
        };
        if (!valid)
        {
            throw Fail($"El valor de «{Show(p.Id)}» no corresponde a su tipo o queda fuera de rango/opciones.");
        }

        if (p.Type == TemplateParameterType.Text)
        {
            ValidateTemplateText(value!.GetValue<string>());
        }
    }

    private static void ValidateTemplateText(string text)
    {
        if (text.Length > TemplateLimits.MaxTextLength || text.Count(c => c is '\n' or '\u2028' or '\u2029') >= TemplateLimits.MaxTextLines)
        {
            throw Fail("text admite hasta 10 000 caracteres y 200 líneas.");
        }

        if (text.Any(c => char.IsControl(c) && c is not '\n' and not '\t'))
        {
            throw Fail("text tiene caracteres de control: solo admite saltos de línea y tabulaciones.");
        }
    }

    internal static TemplateInstance ValidateTemplateBody(TemplateKind kind, JsonObject body)
    {
        if (kind == TemplateKind.Transition)
        {
            using var stream = TemplateJsonStream(body);
            using (JsonDocument parsed = Parse(stream, TransitionPreset.Format))
            {
                if (Header(parsed.RootElement, TransitionPreset.Format, TransitionPreset.Version) != 2)
                {
                    throw Fail("El body de transition necesita rodaje.preset v2.");
                }
            }

            stream.Position = 0;
            return new TemplateInstance(kind, [], ReadTransition(stream), null);
        }

        if (kind == TemplateKind.Effect)
        {
            using var stream = TemplateJsonStream(body);
            return new TemplateInstance(kind, [], null, ReadEffect(stream));
        }

        if (body["layers"] is not JsonArray layers || layers.Count is < 1 or > TemplateLimits.MaxLayers)
        {
            throw Fail("body.layers necesita de 1 a 16 capas.");
        }

        var result = new List<InstantiatedTemplateLayer>(layers.Count);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonNode? node in layers)
        {
            if (node is not JsonObject layer)
            {
                throw Fail("Cada capa tiene que ser un objeto.");
            }

            // El documento original y los valores ya se comprobaron como Unicode válido.
            // Solo se serializa el estilo, que tiene que pasar por su lector completo.
            string id = TemplateIdentifier(NodeString(layer["id"]), ids, "layers");
            string text = NodeString(layer["text"]) ?? throw Fail("Falta text en la capa.");
            ValidateTemplateText(text);
            double start = TemplateNumber(layer, "start", 0, 0, TemplateLimits.MaxSeconds);
            double end = TemplateNumber(layer, "end", 0, 0, TemplateLimits.MaxSeconds);
            double x = TemplateNumber(layer, "x", null, -TemplateLimits.MaxPosition, TemplateLimits.MaxPosition);
            double y = TemplateNumber(layer, "y", null, -TemplateLimits.MaxPosition, TemplateLimits.MaxPosition);
            TemplateBox? box = null;
            if (layer["box"] is { } boxNode)
            {
                if (boxNode is not JsonObject b)
                {
                    throw Fail("box tiene que ser un objeto.");
                }

                box = new TemplateBox(TemplateNumber(b, "widthFraction", null, TemplateLimits.MinBoxWidthFraction, TemplateLimits.MaxBoxWidthFraction));
            }
            if (layer["style"] is not JsonObject style)
            {
                throw Fail("style tiene que ser un estilo completo.");
            }

            string styleJson = style.ToJsonString();
            using var styleStream = new MemoryStream(Encoding.UTF8.GetBytes(styleJson));
            TextStylePreset definition = ReadTextStyle(styleStream);
            result.Add(new InstantiatedTemplateLayer(id, text, definition, styleJson, x, y, start, end, box));
        }

        return new TemplateInstance(kind, result.AsReadOnly(), null, null);
    }

    private static MemoryStream TemplateJsonStream(JsonNode node) => new(Encoding.UTF8.GetBytes(node.ToJsonString()));

    private static double TemplateNumber(JsonObject owner, string property, double? fallback, double min, double max)
    {
        if (!owner.TryGetPropertyValue(property, out JsonNode? node))
        {
            return fallback ?? throw Fail($"Falta {property}.");
        }

        if (node?.GetValueKind() != JsonValueKind.Number
            || !double.TryParse(node.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            || !double.IsFinite(value) || value < min || value > max)
        {
            throw Fail($"{property} tiene que ser un número dentro de rango.");
        }

        return value;
    }
}

/// <summary>Sustituye los valores publicados en una copia y vuelve a pasar por los lectores del contenido.</summary>
public static class TemplateInstantiator
{
    public static TemplateInstance Instantiate(TemplateDocument document, IReadOnlyDictionary<string, JsonNode?>? values = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        try
        {
            return InstantiateBody(document, values);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            throw new PackageContentException("Los valores de la plantilla no son JSON válido.", ex);
        }
    }

    /// <summary>Con los valores como JSON de un objeto (la opción <c>template.values</c> del proyecto); nulo o vacío, los de la plantilla.</summary>
    public static TemplateInstance Instantiate(TemplateDocument document, string? valuesJson)
    {
        if (string.IsNullOrEmpty(valuesJson))
        {
            return Instantiate(document);
        }

        JsonObject parsed;
        try
        {
            parsed = JsonNode.Parse(valuesJson) as JsonObject ?? throw new PackageContentException("Los valores de la plantilla no son un objeto JSON.");
        }
        catch (JsonException ex)
        {
            throw new PackageContentException("Los valores de la plantilla no son JSON válido.", ex);
        }

        return Instantiate(document, parsed.ToDictionary(p => p.Key, p => p.Value));
    }

    private static TemplateInstance InstantiateBody(TemplateDocument document, IReadOnlyDictionary<string, JsonNode?>? values)
    {
        JsonObject body = document.Body;
        foreach (TemplateParameter p in document.Parameters)
        {
            if (values is null || !values.TryGetValue(p.Id, out JsonNode? value))
            {
                continue;
            }

            CheckValue(value, 0);
            PresetReader.ValidateTemplateValue(p, value, supplied: true);
            foreach (string path in p.Targets)
            {
                (JsonNode parent, string segment) = PresetReader.ResolveTemplateTarget(body, path);
                if (parent is JsonObject obj)
                {
                    obj[segment] = value!.DeepClone();
                }
                else
                {
                    parent.AsArray()[int.Parse(segment, CultureInfo.InvariantCulture)] = value!.DeepClone();
                }
            }
        }

        return PresetReader.ValidateTemplateBody(document.Kind, body);
    }

    // Antes de clonar/serializar: ni profundidad arbitraria ni sustitutos UTF-16 que el serializador reemplazaría.
    private static void CheckValue(JsonNode? value, int depth)
    {
        if (depth > 16)
        {
            throw new PackageContentException("Un valor de la plantilla supera la profundidad JSON permitida.");
        }

        if (value is JsonObject obj)
        {
            foreach ((string key, JsonNode? child) in obj)
            {
                _ = SafeText.StrictUtf8.GetByteCount(key);
                CheckValue(child, depth + 1);
            }
        }
        else if (value is JsonArray array)
        {
            foreach (JsonNode? child in array)
            {
                CheckValue(child, depth + 1);
            }
        }
        else if (value?.GetValueKind() == JsonValueKind.String)
        {
            _ = SafeText.StrictUtf8.GetByteCount(value.GetValue<string>());
        }
    }
}
