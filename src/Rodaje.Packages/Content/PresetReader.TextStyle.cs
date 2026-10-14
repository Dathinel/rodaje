using System.Globalization;
using System.Text.Json;

namespace Rodaje.Packages.Content;

/// <summary>Lectura de <c>rodaje.textstyle</c> v1 y v2, con las mismas reglas que los otros preajustes.</summary>
internal static partial class PresetReader
{
    public const double MaxTextSize = 1000;
    public const double MaxShadowBlur = 64;
    public const double MaxStyleOffset = 1000;
    public const double MaxStyleAnimationSeconds = 60;
    public const int MaxFallbackFonts = 8;
    public const int MaxFamilyLength = 128;

    private static readonly System.Buffers.SearchValues<char> HexDigits = System.Buffers.SearchValues.Create("0123456789abcdefABCDEF");
    private static readonly System.Buffers.SearchValues<char> FeatureChars = System.Buffers.SearchValues.Create("abcdefghijklmnopqrstuvwxyz0123456789 ");

    public static TextStylePreset ReadTextStyle(Stream stream)
    {
        using JsonDocument document = Parse(stream, TextStylePreset.Format);
        JsonElement root = document.RootElement;
        Header(root, TextStylePreset.Format, TextStylePreset.Version);
        (string id, string name) = Identity(root);
        TextStyleFont font = Font(root);
        double size = Number(root, "size", null, double.Epsilon, MaxTextSize, "size");
        (int width, int height) = Reference(root);
        double lineHeight = Number(root, "lineHeight", 1.2, double.Epsilon, 10, "lineHeight");
        double letterSpacing = Number(root, "letterSpacing", 0, -MaxStyleOffset, MaxStyleOffset, "letterSpacing");
        TextStyleAlign align = String(root, "align") switch
        {
            null or "center" => TextStyleAlign.Center,
            "left" => TextStyleAlign.Left,
            "right" => TextStyleAlign.Right,
            { } other => throw Fail($"align «{Show(other)}» no existe: left, center o right."),
        };

        string fill = "#FFFFFF";
        TextStyleGradient? gradient = null;
        if (Section(root, "fill") is { } fillElement)
        {
            fill = Color(fillElement, "fill.color", required: true)!;
            gradient = Gradient(fillElement);
        }

        TextStyleStroke? stroke = Section(root, "stroke") is { } s ? Stroke(s, "stroke", size) : null;
        TextStyleShadow? shadow = Section(root, "shadow") is { } sh ? Shadow(sh, "shadow") : null;
        List<TextStyleStroke> extraStrokes = List(root, "extraStrokes", MaxExtraLayers, (e, at) => Stroke(e, at, size));
        List<TextStyleShadow> extraShadows = List(root, "extraShadows", MaxExtraLayers, Shadow);


        TextStyleBackground? background = null;
        if (Section(root, "background") is { } b)
        {
            (double padX, double padY) = Padding(b);
            background = new TextStyleBackground(
                Color(b, "background.color", required: true)!,
                padX,
                padY,
                Number(b, "radius", 0, 0, MaxStyleOffset, "background.radius"));
        }

        string? highlight = null;
        string highlightMode = "word";
        if (Section(root, "highlight") is { } h)
        {
            highlight = Color(h, "highlight.color", required: true);
            highlightMode = Choice(h, "mode", "highlight.mode", "word", "word", "sweep");
        }

        TextStyleTypography? typography = null;
        if (Section(root, "typography") is { } t)
        {
            bool kerning = !t.TryGetProperty("kerning", out JsonElement k) || k.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => throw Fail("typography.kerning tiene que ser true o false."),
            };
            List<string> features = List(t, "features", MaxFeatures, (e, at) =>
            {
                string tag = e.ValueKind == JsonValueKind.String ? e.GetString()! : throw Fail($"{at} tiene que ser un texto.");
                return FeatureProblem(tag) is { } problem
                    ? throw Fail($"{at} tiene que ser una etiqueta OpenType de 4 caracteres [a-z0-9 ] (con «-» delante para apagarla): {problem}.")
                    : tag;
            });
            typography = new TextStyleTypography(kerning, Choice(t, "case", "typography.case", "none", "none", "upper", "lower", "smallCaps"),
                features, Choice(t, "fit", "typography.fit", "none", "none", "shrink", "fill"));
        }

        double? bend = null;
        if (Section(root, "path") is { } path)
        {
            Choice(path, "kind", "path.kind", "arc", "arc");
            bend = Number(path, "bend", null, -360, 360, "path.bend");
        }

        return new TextStylePreset(
            id, name, font, size, width, height, lineHeight, letterSpacing, align, fill, stroke, shadow, background,
            Animation(root, "in"), Animation(root, "out"), highlight)
        {
            Gradient = gradient,
            ExtraStrokes = extraStrokes,
            ExtraShadows = extraShadows,
            Typography = typography,
            PathBend = bend,
            HighlightMode = highlightMode,
        };
    }

    public const int MaxExtraLayers = 4;
    public const int MaxFeatures = 16;
    public const int MaxGradientStops = 8;

    private static TextStyleStroke Stroke(JsonElement s, string where, double size)
    {
        if (s.ValueKind != JsonValueKind.Object)
        {
            throw Fail($"{where} tiene que ser un objeto.");
        }

        TextStyleJoin join = String(s, "join") switch
        {
            null or "round" => TextStyleJoin.Round,
            "miter" => TextStyleJoin.Miter,
            "bevel" => TextStyleJoin.Bevel,
            { } other => throw Fail($"{where}.join «{Show(other)}» no existe: round, miter o bevel."),
        };
        return new TextStyleStroke(Color(s, $"{where}.color", required: true)!, Number(s, "width", null, 0, size / 2, $"{where}.width (como mucho la mitad de size)"), join);
    }

    private static TextStyleShadow Shadow(JsonElement sh, string where)
    {
        if (sh.ValueKind != JsonValueKind.Object)
        {
            throw Fail($"{where} tiene que ser un objeto.");
        }

        return new TextStyleShadow(
            Color(sh, $"{where}.color", required: true)!,
            Number(sh, "dx", 0, -MaxStyleOffset, MaxStyleOffset, $"{where}.dx"),
            Number(sh, "dy", 0, -MaxStyleOffset, MaxStyleOffset, $"{where}.dy"),
            Number(sh, "blur", 0, 0, MaxShadowBlur, $"{where}.blur"));
    }

    private static TextStyleGradient? Gradient(JsonElement fill)
    {
        if (Section(fill, "gradient") is not { } g)
        {
            return null;
        }

        string kind = Choice(g, "kind", "fill.gradient.kind", "linear", "linear", "radial");
        double angle = Number(g, "angle", 0, -360, 360, "fill.gradient.angle");
        List<TextStyleGradientStop> stops = List(g, "stops", MaxGradientStops, (e, at) =>
            e.ValueKind == JsonValueKind.Object
                ? new TextStyleGradientStop(Number(e, "position", null, 0, 1, $"{at}.position"), Color(e, $"{at}.color", required: true)!)
                : throw Fail($"{at} tiene que ser un objeto."));
        if (stops.Count < 2)
        {
            throw Fail("fill.gradient.stops necesita al menos dos paradas.");
        }

        for (int i = 1; i < stops.Count; i++)
        {
            if (stops[i].Position < stops[i - 1].Position)
            {
                throw Fail($"fill.gradient.stops[{i}].position tiene que ser mayor o igual que la anterior.");
            }
        }

        return new TextStyleGradient(kind, angle, stops);
    }

    private static List<T> List<T>(JsonElement owner, string property, int max, Func<JsonElement, string, T> read)
    {
        var result = new List<T>();
        if (!owner.TryGetProperty(property, out JsonElement list) || list.ValueKind == JsonValueKind.Null)
        {
            return result;
        }

        if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() > max)
        {
            throw Fail($"{property} tiene que ser una lista de hasta {max.ToString(CultureInfo.InvariantCulture)}.");
        }

        int index = 0;
        foreach (JsonElement e in list.EnumerateArray())
        {
            result.Add(read(e, $"{property}[{index.ToString(CultureInfo.InvariantCulture)}]"));
            index++;
        }

        return result;
    }

    private static string? FeatureProblem(string tag)
    {
        ReadOnlySpan<char> body = tag.StartsWith('-') ? tag.AsSpan(1) : tag.AsSpan();
        if (body.Length != 4)
        {
            return $"tiene {body.Length.ToString(CultureInfo.InvariantCulture)} sin contar el «-»";
        }

        if (body.ContainsAnyExcept(FeatureChars))
        {
            return "solo admite minúsculas, cifras y espacios";
        }

        // En OpenType el espacio solo rellena por el final.
        return body[0] == ' ' || body.TrimEnd(' ').Contains(' ') ? "los espacios solo van al final, de relleno" : null;
    }

    private static TextStyleFont Font(JsonElement root)
    {
        JsonElement f = Section(root, "font") ?? throw Fail("Falta font.");
        string family = String(f, "family") is { } raw ? SafeText.Clean(raw).Trim() : "";
        if (family.Length is 0 or > MaxFamilyLength)
        {
            throw Fail($"font.family tiene que tener de 1 a {MaxFamilyLength} caracteres.");
        }

        double weight = Number(f, "weight", 400, 100, 900, "font.weight");
        if (weight != Math.Floor(weight))
        {
            throw Fail("font.weight tiene que ser un entero de 100 a 900.");
        }

        bool italic = false;
        if (f.TryGetProperty("italic", out JsonElement i))
        {
            italic = i.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => throw Fail("font.italic tiene que ser true o false."),
            };
        }

        string? source = String(f, "source");
        if (source is not null && !PackageReference.TryParse(source, out _))
        {
            throw Fail($"font.source «{Show(source)}» no es una referencia paquete@versión/elemento.");
        }

        var fallback = new List<string>();
        if (f.TryGetProperty("fallback", out JsonElement list))
        {
            if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() > MaxFallbackFonts)
            {
                throw Fail($"font.fallback tiene que ser una lista de hasta {MaxFallbackFonts} familias.");
            }

            foreach (JsonElement e in list.EnumerateArray())
            {
                string name = e.ValueKind == JsonValueKind.String ? SafeText.Clean(e.GetString()!).Trim() : "";
                if (name.Length is 0 or > MaxFamilyLength)
                {
                    throw Fail("font.fallback tiene una familia vacía o demasiado larga.");
                }

                fallback.Add(name);
            }
        }

        return new TextStyleFont(family, (int)weight, italic, source, fallback);
    }

    private static TextStyleAnimation? Animation(JsonElement root, string property)
    {
        if (Section(root, property) is not { } a)
        {
            return null;
        }

        double duration = Number(a, "durationSeconds", null, double.Epsilon, MaxStyleAnimationSeconds, $"{property}.durationSeconds");
        List<PresetCurve> curves = a.TryGetProperty("curves", out JsonElement c) ? StyleTracks(c, $"{property}.curves", duration) : [];
        return new TextStyleAnimation(duration, curves)
        {
            Unit = Choice(a, "unit", $"{property}.unit", "block", "block", "line", "word", "letter"),
            Stagger = Number(a, "stagger", 0, 0, 1, $"{property}.stagger"),
            Order = Choice(a, "order", $"{property}.order", "forward", "forward", "backward", "center", "edges", "random"),
        };
    }

    /// <summary>Como las curvas de la transformación, más <c>blur</c> (radio de 0 a 64) para la animación por unidades.</summary>
    private static List<PresetCurve> StyleTracks(JsonElement tracks, string where, double duration)
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
            (double Min, double Max) range = p.Name == "blur" ? (0, MaxShadowBlur) : TransformRange(p.Name, at);
            if (!seen.Add(p.Name))
            {
                throw Fail($"{at} está repetido.");
            }

            curves.Add(new PresetCurve(Transform, p.Name, Keys(p.Value, at, duration, range)));
        }

        return curves;
    }


    private static (double X, double Y) Padding(JsonElement background)
    {
        if (!background.TryGetProperty("padding", out JsonElement p))
        {
            return (0, 0);
        }

        if (p.ValueKind != JsonValueKind.Array || p.GetArrayLength() != 2)
        {
            throw Fail("background.padding tiene que ser una lista de dos números (horizontal y vertical).");
        }

        double[] values = new double[2];
        int index = 0;
        foreach (JsonElement e in p.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Number || !double.IsFinite(e.GetDouble()) || e.GetDouble() < 0 || e.GetDouble() > MaxStyleOffset)
            {
                throw Fail($"background.padding tiene valores fuera de 0 a {MaxStyleOffset.ToString(CultureInfo.InvariantCulture)}.");
            }

            values[index++] = e.GetDouble();
        }

        return (values[0], values[1]);
    }

    private static JsonElement? Section(JsonElement owner, string property)
    {
        if (!owner.TryGetProperty(property, out JsonElement e) || e.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return e.ValueKind == JsonValueKind.Object ? e : throw Fail($"{property} tiene que ser un objeto.");
    }

    private static double Number(JsonElement owner, string property, double? fallback, double min, double max, string where)
    {
        if (!owner.TryGetProperty(property, out JsonElement e))
        {
            return fallback ?? throw Fail($"Falta {where}.");
        }

        if (e.ValueKind != JsonValueKind.Number || !double.IsFinite(e.GetDouble()))
        {
            throw Fail($"{where} tiene que ser un número.");
        }

        double value = e.GetDouble();
        return value >= min && value <= max
            ? value
            : throw Fail($"{where} queda fuera de rango ({Format(min)} a {Format(max)}).");

        static string Format(double v) => v == double.Epsilon ? "más de 0" : v.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Color <c>#RRGGBB</c> o <c>#RRGGBBAA</c>, devuelto en mayúsculas.</summary>
    private static string? Color(JsonElement owner, string where, bool required)
    {
        string? text = String(owner, "color");
        if (text is null)
        {
            return required ? throw Fail($"Falta {where}.") : null;
        }

        bool valid = text.Length is 7 or 9 && text[0] == '#' && !text.AsSpan(1).ContainsAnyExcept(HexDigits);
        return valid ? text.ToUpperInvariant() : throw Fail($"{where} «{Show(text)}» no es un color #RRGGBB o #RRGGBBAA.");
    }
}
