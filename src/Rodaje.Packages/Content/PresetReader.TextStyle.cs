using System.Globalization;
using System.Text.Json;

namespace Rodaje.Packages.Content;

/// <summary>Lectura de <c>rodaje.textstyle</c> v1, con las mismas reglas que los otros preajustes.</summary>
internal static partial class PresetReader
{
    public const double MaxTextSize = 1000;
    public const double MaxShadowBlur = 64;
    public const double MaxStyleOffset = 1000;
    public const double MaxStyleAnimationSeconds = 60;
    public const int MaxFallbackFonts = 8;
    public const int MaxFamilyLength = 128;

    private static readonly System.Buffers.SearchValues<char> HexDigits = System.Buffers.SearchValues.Create("0123456789abcdefABCDEF");

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
        if (Section(root, "fill") is { } fillElement)
        {
            fill = Color(fillElement, "fill.color", required: true)!;
        }

        TextStyleStroke? stroke = null;
        if (Section(root, "stroke") is { } s)
        {
            TextStyleJoin join = String(s, "join") switch
            {
                null or "round" => TextStyleJoin.Round,
                "miter" => TextStyleJoin.Miter,
                "bevel" => TextStyleJoin.Bevel,
                { } other => throw Fail($"stroke.join «{Show(other)}» no existe: round, miter o bevel."),
            };
            stroke = new TextStyleStroke(Color(s, "stroke.color", required: true)!, Number(s, "width", null, 0, size / 2, "stroke.width (como mucho la mitad de size)"), join);
        }

        TextStyleShadow? shadow = null;
        if (Section(root, "shadow") is { } sh)
        {
            shadow = new TextStyleShadow(
                Color(sh, "shadow.color", required: true)!,
                Number(sh, "dx", 0, -MaxStyleOffset, MaxStyleOffset, "shadow.dx"),
                Number(sh, "dy", 0, -MaxStyleOffset, MaxStyleOffset, "shadow.dy"),
                Number(sh, "blur", 0, 0, MaxShadowBlur, "shadow.blur"));
        }

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

        return new TextStylePreset(
            id, name, font, size, width, height, lineHeight, letterSpacing, align, fill, stroke, shadow, background,
            Animation(root, "in"), Animation(root, "out"));
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
        List<PresetCurve> curves = a.TryGetProperty("curves", out JsonElement c) ? Tracks(c, $"{property}.curves", duration) : [];
        return new TextStyleAnimation(duration, curves);
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
