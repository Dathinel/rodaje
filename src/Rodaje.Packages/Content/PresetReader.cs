using System.Globalization;
using System.Text.Json;

namespace Rodaje.Packages.Content;

/// <summary>Lectura y validación de <c>rodaje.filter</c>, <c>rodaje.effect</c> y <c>rodaje.preset</c>.</summary>
internal static partial class PresetReader
{
    public const int MaxFileBytes = 1 << 20;
    public const int MaxEffects = 32;
    public const int MaxCurves = 64;
    public const int MaxKeys = 1000;
    public const int MaxValues = 16;
    public const int MaxOptionLength = 256;
    public const double MaxEffectSeconds = 600;
    public const double MaxTransitionSeconds = 60;
    public const int MinReference = 16;
    public const int MaxReference = 16384;
    public const string Transform = "rodaje.transform";
    public const string Crossfade = "rodaje.crossfade";

    /// <summary>Parámetros de <c>rodaje.transform</c> con su rango (iguales a los del registro de efectos del núcleo).</summary>
    private static readonly Dictionary<string, (double Min, double Max)> TransformParameters = new(StringComparer.Ordinal)
    {
        ["x"] = (double.NegativeInfinity, double.PositiveInfinity),
        ["y"] = (double.NegativeInfinity, double.PositiveInfinity),
        ["scale"] = (0, 100),
        ["rotation"] = (double.NegativeInfinity, double.PositiveInfinity),
        ["opacity"] = (0, 1),
    };

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        MaxDepth = 16,
    };

    public static FilterPreset ReadFilter(Stream stream)
    {
        using JsonDocument document = Parse(stream, FilterPreset.Format);
        JsonElement root = document.RootElement;
        Header(root, FilterPreset.Format, FilterPreset.Version);
        (string id, string name) = Identity(root);
        List<PresetEffect> effects = Effects(root, required: true);
        return new FilterPreset(id, name, effects);
    }

    public static EffectPreset ReadEffect(Stream stream)
    {
        using JsonDocument document = Parse(stream, EffectPreset.Format);
        JsonElement root = document.RootElement;
        Header(root, EffectPreset.Format, EffectPreset.Version);
        (string id, string name) = Identity(root);
        EffectPlacement placement = String(root, "anchor") switch
        {
            "start" => EffectPlacement.In,
            "end" => EffectPlacement.Out,
            "whole" => EffectPlacement.Whole,
            null => throw Fail("Falta anchor: start, end o whole."),
            _ => throw Fail("anchor tiene que ser start, end o whole."),
        };
        double duration = Duration(root, MaxEffectSeconds);
        (int width, int height) = Reference(root);
        List<PresetEffect> effects = Effects(root, required: false);

        var curves = new List<PresetCurve>();
        var seen = new HashSet<(string, string)>();
        if (root.TryGetProperty("curves", out JsonElement list))
        {
            if (list.ValueKind != JsonValueKind.Array)
            {
                throw Fail("curves tiene que ser una lista.");
            }

            if (list.GetArrayLength() > MaxCurves)
            {
                throw Fail($"Hay más de {MaxCurves} curvas.");
            }

            int index = 0;
            foreach (JsonElement e in list.EnumerateArray())
            {
                string where = $"curves[{index++}]";
                if (e.ValueKind != JsonValueKind.Object)
                {
                    throw Fail($"{where} tiene que ser un objeto.");
                }

                string effect = EffectType(e, "effect", where);
                if (effect != Transform && !effects.Any(x => x.Type == effect))
                {
                    throw Fail($"{where} anima «{effect}», que no está en effects (solo rodaje.transform puede ir sin estar).");
                }

                string parameter = String(e, "parameter") ?? throw Fail($"{where} necesita parameter.");
                if (!IsParameterName(parameter))
                {
                    throw Fail($"{where}.parameter «{Show(parameter)}» no es un nombre de parámetro válido.");
                }

                if (!seen.Add((effect, parameter)))
                {
                    throw Fail($"{where} repite la curva de {effect}/{parameter}.");
                }

                if (!e.TryGetProperty("keys", out JsonElement keys))
                {
                    throw Fail($"{where} necesita keys.");
                }

                (double Min, double Max) range = effect == Transform ? TransformRange(parameter, where) : (double.NegativeInfinity, double.PositiveInfinity);
                curves.Add(new PresetCurve(effect, parameter, Keys(keys, $"{where}.keys", duration, range)));
            }
        }

        if (effects.Count == 0 && curves.Count == 0)
        {
            throw Fail("El efecto no trae effects ni curves.");
        }

        return new EffectPreset(id, name, placement, duration, width, height, effects, curves);
    }

    public static TransitionPreset ReadTransition(Stream stream)
    {
        using JsonDocument document = Parse(stream, TransitionPreset.Format);
        JsonElement root = document.RootElement;
        int version = Header(root, TransitionPreset.Format, TransitionPreset.Version);
        (string id, string name) = Identity(root);
        if (String(root, "kind") is { } kind && kind != "transition")
        {
            throw Fail($"El preajuste es de tipo «{Show(kind)}», no una transición.");
        }

        double duration = Duration(root, MaxTransitionSeconds);
        (int width, int height) = Reference(root);
        if (version == 1)
        {
            // «Guardar como transición» de la fase 4: la animación del clip que entra, con fundido cruzado.
            string effect = root.TryGetProperty("effect", out _) ? EffectType(root, "effect", "effect") : Transform;
            if (effect != Transform)
            {
                throw Fail($"effect tiene que ser {Transform}.");
            }

            if (!root.TryGetProperty("tracks", out JsonElement tracks))
            {
                throw Fail("Falta tracks.");
            }

            return new TransitionPreset(id, name, duration, Crossfade, width, height, [], Tracks(tracks, "tracks", duration));
        }

        string mix = root.TryGetProperty("mix", out _) ? EffectType(root, "mix", "mix") : Crossfade;
        if (mix == TransitionPreset.Format)
        {
            throw Fail("mix no puede ser rodaje.preset.");
        }

        IReadOnlyList<PresetCurve> outgoing = root.TryGetProperty("outgoing", out JsonElement o) ? Tracks(o, "outgoing", duration) : [];
        IReadOnlyList<PresetCurve> incoming = root.TryGetProperty("incoming", out JsonElement i) ? Tracks(i, "incoming", duration) : [];
        return new TransitionPreset(id, name, duration, mix, width, height, outgoing, incoming)
        {
            MixParameters = Numbers(root, "mixParameters", "mixParameters"),
            MixOptions = Options(root, "mixOptions", "mixOptions"),
        };
    }

    private static JsonDocument Parse(Stream stream, string format)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (stream.CanSeek && stream.Length - stream.Position > MaxFileBytes)
        {
            throw Fail($"El archivo de {format} pesa más de 1 MB.");
        }

        using var buffer = new MemoryStream();
        byte[] chunk = new byte[16 * 1024];
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxFileBytes)
            {
                throw Fail($"El archivo de {format} pesa más de 1 MB.");
            }
        }

        JsonDocument document;
        try
        {
            // Una marca BOM inicial se salta, como al leer los elementos propios (SafeText.ReadAllTextStrict): si no, la
            // biblioteca listaría un estilo o unas zonas que luego no se pueden leer.
            ReadOnlyMemory<byte> json = buffer.GetBuffer().AsMemory(0, (int)buffer.Length);
            if (json.Span.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
            {
                json = json[3..];
            }

            document = JsonDocument.Parse(json, DocumentOptions);
        }
        catch (JsonException ex)
        {
            throw new PackageContentException($"No es JSON válido: {ex.Message}", ex);
        }

        if (SafeText.HasUnreadableString(document.RootElement))
        {
            document.Dispose();
            throw Fail("El archivo tiene un texto que no es Unicode válido (UTF-8 dañado o un carácter suelto escapado).");
        }

        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            document.Dispose();
            throw Fail("El archivo tiene que ser un objeto JSON.");
        }

        return document;
    }

    private static int Header(JsonElement root, string format, int maxVersion)
    {
        string? actual = String(root, "format");
        if (actual != format)
        {
            throw Fail(actual is null ? $"Falta format: se esperaba «{format}»." : $"format es «{Show(actual)}» y se esperaba «{format}».");
        }

        if (!root.TryGetProperty("version", out JsonElement v) || v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out int version) || version < 1)
        {
            throw Fail("version tiene que ser un entero desde 1.");
        }

        if (version > maxVersion)
        {
            throw Fail($"Es la versión {version.ToString(CultureInfo.InvariantCulture)} de {format} y este programa entiende hasta la {maxVersion.ToString(CultureInfo.InvariantCulture)}: actualiza el programa.");
        }

        return version;
    }

    private static (string Id, string Name) Identity(JsonElement root)
    {
        string id = "";
        if (root.TryGetProperty("id", out JsonElement idElement))
        {
            id = idElement.ValueKind == JsonValueKind.String ? idElement.GetString()! : "";
            if (!Identifiers.IsValid(id))
            {
                throw Fail($"id «{Show(id)}» no es válido: minúsculas, dígitos y guiones o puntos sueltos entre ellos.");
            }
        }

        string name = String(root, "name") is { } raw ? SafeText.Clean(raw) : "";
        if (name.Length == 0)
        {
            throw Fail("Falta name.");
        }

        if (name.Length > SafeText.MaxName)
        {
            throw Fail($"name pasa de {SafeText.MaxName} caracteres.");
        }

        return (id, name);
    }

    private static double Duration(JsonElement root, double max)
    {
        if (!root.TryGetProperty("durationSeconds", out JsonElement d) || d.ValueKind != JsonValueKind.Number)
        {
            throw Fail("Falta durationSeconds.");
        }

        double seconds = d.GetDouble();
        if (!(seconds > 0 && seconds <= max))
        {
            throw Fail($"durationSeconds tiene que ser mayor que 0 y de hasta {max.ToString(CultureInfo.InvariantCulture)} s.");
        }

        return seconds;
    }

    private static (int Width, int Height) Reference(JsonElement root)
    {
        if (!root.TryGetProperty("reference", out JsonElement r))
        {
            return (1920, 1080);
        }

        if (r.ValueKind != JsonValueKind.Object
            || !r.TryGetProperty("width", out JsonElement w) || w.ValueKind != JsonValueKind.Number || !w.TryGetInt32(out int width)
            || !r.TryGetProperty("height", out JsonElement h) || h.ValueKind != JsonValueKind.Number || !h.TryGetInt32(out int height)
            || width is < MinReference or > MaxReference || height is < MinReference or > MaxReference)
        {
            throw Fail($"reference necesita width y height enteros de {MinReference} a {MaxReference}.");
        }

        return (width, height);
    }

    private static List<PresetEffect> Effects(JsonElement root, bool required)
    {
        if (!root.TryGetProperty("effects", out JsonElement list))
        {
            return required ? throw Fail("Falta effects.") : [];
        }

        if (list.ValueKind != JsonValueKind.Array)
        {
            throw Fail("effects tiene que ser una lista.");
        }

        int count = list.GetArrayLength();
        if (required && count == 0)
        {
            throw Fail("effects está vacía.");
        }

        if (count > MaxEffects)
        {
            throw Fail($"Hay más de {MaxEffects} efectos.");
        }

        var effects = new List<PresetEffect>(count);
        int index = 0;
        foreach (JsonElement e in list.EnumerateArray())
        {
            string where = $"effects[{index++}]";
            if (e.ValueKind != JsonValueKind.Object)
            {
                throw Fail($"{where} tiene que ser un objeto.");
            }

            string type = EffectType(e, "type", where);
            Dictionary<string, double> parameters = Numbers(e, "parameters", where + ".parameters");
            if (type == Transform)
            {
                foreach ((string parameter, double value) in parameters)
                {
                    if (TransformParameters.TryGetValue(parameter, out (double Min, double Max) range) && (value < range.Min || value > range.Max))
                    {
                        throw Fail($"{where}.parameters.{parameter} queda fuera de {range.Min.ToString(CultureInfo.InvariantCulture)} a {range.Max.ToString(CultureInfo.InvariantCulture)}.");
                    }
                }
            }

            effects.Add(new PresetEffect(type, parameters, Options(e, "options", where + ".options")));
        }

        return effects;
    }

    private static Dictionary<string, double> Numbers(JsonElement owner, string property, string where)
    {
        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        if (!owner.TryGetProperty(property, out JsonElement e))
        {
            return values;
        }

        if (e.ValueKind != JsonValueKind.Object)
        {
            throw Fail($"{where} tiene que ser un objeto.");
        }

        foreach (JsonProperty p in e.EnumerateObject())
        {
            if (!IsParameterName(p.Name))
            {
                throw Fail($"{where}: «{Show(p.Name)}» no es un nombre de parámetro válido.");
            }

            if (p.Value.ValueKind != JsonValueKind.Number || !double.IsFinite(p.Value.GetDouble()))
            {
                throw Fail($"{where}.{p.Name} tiene que ser un número.");
            }

            if (!values.TryAdd(p.Name, p.Value.GetDouble()))
            {
                throw Fail($"{where}.{p.Name} está repetido.");
            }

            if (values.Count > MaxValues)
            {
                throw Fail($"{where} tiene más de {MaxValues} valores.");
            }
        }

        return values;
    }

    private static Dictionary<string, string> Options(JsonElement owner, string property, string where)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!owner.TryGetProperty(property, out JsonElement e))
        {
            return values;
        }

        if (e.ValueKind != JsonValueKind.Object)
        {
            throw Fail($"{where} tiene que ser un objeto.");
        }

        foreach (JsonProperty p in e.EnumerateObject())
        {
            if (!IsParameterName(p.Name))
            {
                throw Fail($"{where}: «{Show(p.Name)}» no es un nombre de opción válido.");
            }

            if (p.Value.ValueKind != JsonValueKind.String)
            {
                throw Fail($"{where}.{p.Name} tiene que ser texto.");
            }

            string text = SafeText.Clean(p.Value.GetString()!);
            if (text.Length > MaxOptionLength)
            {
                throw Fail($"{where}.{p.Name} pasa de {MaxOptionLength} caracteres.");
            }

            if (!values.TryAdd(p.Name, text))
            {
                throw Fail($"{where}.{p.Name} está repetido.");
            }

            if (values.Count > MaxValues)
            {
                throw Fail($"{where} tiene más de {MaxValues} opciones.");
            }
        }

        return values;
    }

    /// <summary>Curvas de la transformación por parámetro: <c>{ "x": [ {t, value, interp} ] }</c>.</summary>
    private static List<PresetCurve> Tracks(JsonElement tracks, string where, double duration)
    {
        if (tracks.ValueKind != JsonValueKind.Object)
        {
            throw Fail($"{where} tiene que ser un objeto.");
        }

        var curves = new List<PresetCurve>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty p in tracks.EnumerateObject())
        {
            string at = $"{where}.{Show(p.Name)}";
            (double Min, double Max) range = TransformRange(p.Name, at);
            if (!seen.Add(p.Name))
            {
                throw Fail($"{at} está repetido.");
            }

            curves.Add(new PresetCurve(Transform, p.Name, Keys(p.Value, at, duration, range)));
        }

        return curves;
    }

    private static (double Min, double Max) TransformRange(string parameter, string where) =>
        TransformParameters.TryGetValue(parameter, out (double Min, double Max) range)
            ? range
            : throw Fail($"{where}: «{Show(parameter)}» no es un parámetro de la transformación (x, y, scale, rotation u opacity).");

    private static List<PresetKey> Keys(JsonElement list, string where, double duration, (double Min, double Max) range)
    {
        if (list.ValueKind != JsonValueKind.Array)
        {
            throw Fail($"{where} tiene que ser una lista de claves.");
        }

        int count = list.GetArrayLength();
        if (count == 0)
        {
            throw Fail($"{where} no tiene claves.");
        }

        if (count > MaxKeys)
        {
            throw Fail($"{where} tiene más de {MaxKeys} claves.");
        }

        const double Slack = 1e-6;
        var keys = new List<PresetKey>(count);
        int index = 0;
        foreach (JsonElement k in list.EnumerateArray())
        {
            string at = $"{where}[{index++}]";
            if (k.ValueKind != JsonValueKind.Object)
            {
                throw Fail($"{at} tiene que ser un objeto con t y value.");
            }

            if (!k.TryGetProperty("t", out JsonElement t) || t.ValueKind != JsonValueKind.Number || !double.IsFinite(t.GetDouble()))
            {
                throw Fail($"{at}.t tiene que ser un número.");
            }

            double time = t.GetDouble();
            if (time < -Slack || time > duration + Slack)
            {
                throw Fail($"{at}.t queda fuera de 0 a durationSeconds.");
            }

            if (keys.Count > 0 && time <= keys[^1].T)
            {
                throw Fail($"{at}.t tiene que ser mayor que el de la clave anterior.");
            }

            if (!k.TryGetProperty("value", out JsonElement v) || v.ValueKind != JsonValueKind.Number || !double.IsFinite(v.GetDouble()))
            {
                throw Fail($"{at}.value tiene que ser un número.");
            }

            double value = v.GetDouble();
            if (value < range.Min || value > range.Max)
            {
                throw Fail($"{at}.value queda fuera de {range.Min.ToString(CultureInfo.InvariantCulture)} a {range.Max.ToString(CultureInfo.InvariantCulture)}.");
            }

            if (k.TryGetProperty("interp", out JsonElement i) && i.ValueKind != JsonValueKind.String)
            {
                throw Fail($"{at}.interp tiene que ser texto: hold, linear, easeIn, easeOut, easeInOut, bezier, bounce o elastic.");
            }

            PresetInterpolation interpolation = String(k, "interp") switch
            {
                null or "linear" => PresetInterpolation.Linear,
                "hold" => PresetInterpolation.Hold,
                "easeIn" => PresetInterpolation.EaseIn,
                "easeOut" => PresetInterpolation.EaseOut,
                "easeInOut" => PresetInterpolation.EaseInOut,
                "bezier" => PresetInterpolation.Bezier,
                "bounce" => PresetInterpolation.Bounce,
                "elastic" => PresetInterpolation.Elastic,
                { } other => throw Fail($"{at}.interp «{Show(other)}» no existe: hold, linear, easeIn, easeOut, easeInOut, bezier, bounce o elastic."),
            };
            (double, double, double, double)? ease = null;
            if (interpolation == PresetInterpolation.Bezier)
            {
                if (!k.TryGetProperty("ease", out JsonElement e) || e.ValueKind != JsonValueKind.Array || e.GetArrayLength() != 4
                    || e.EnumerateArray().Any(n => n.ValueKind != JsonValueKind.Number || !double.IsFinite(n.GetDouble())))
                {
                    throw Fail($"{at}.ease tiene que ser [x1, y1, x2, y2] con «interp»: «bezier».");
                }

                double[] c = [.. e.EnumerateArray().Select(n => n.GetDouble())];
                if (c[0] is < 0 or > 1 || c[2] is < 0 or > 1 || c[1] is < -2 or > 3 || c[3] is < -2 or > 3)
                {
                    throw Fail($"{at}.ease: x1 y x2 van de 0 a 1; y1 e y2, de −2 a 3.");
                }

                ease = (c[0], c[1], c[2], c[3]);
            }

            keys.Add(new PresetKey(Math.Clamp(time, 0, duration), value, interpolation) { Ease = ease });
        }

        return keys;
    }

    /// <summary>Tipo de efecto: <c>rodaje.color</c>, <c>otro.efecto-propio</c>…</summary>
    private static string EffectType(JsonElement owner, string property, string where)
    {
        string? type = String(owner, property);
        if (type is null)
        {
            throw Fail($"{where} necesita {property}.");
        }

        bool valid = type.Length <= 64 && type.Contains('.', StringComparison.Ordinal) && !type.StartsWith('.') && !type.EndsWith('.')
            && type.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '.' or '-')
            && !type.Contains("..", StringComparison.Ordinal);
        return valid ? type : throw Fail($"{where}: «{Show(type)}» no es un tipo de efecto válido (como rodaje.color).");
    }

    private static bool IsParameterName(string name) =>
        name.Length is > 0 and <= 64 && char.IsAsciiLetter(name[0]) && name.All(char.IsAsciiLetterOrDigit);

    private static string? String(JsonElement owner, string property) =>
        owner.TryGetProperty(property, out JsonElement e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;

    private static string Show(string text) => SafeText.Truncate(SafeText.Clean(text), 40);

    private static PackageContentException Fail(string message) => new(message);
}
