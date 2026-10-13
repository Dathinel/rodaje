using System.Globalization;
using System.Text.Json;

namespace Rodaje.Packages.Content;

/// <summary>Lectura y escritura de <c>rodaje.safezones</c> v1, con las mismas reglas que los otros preajustes.</summary>
internal static partial class PresetReader
{
    public const int MaxSafeZones = 32;
    public const int MaxZoneLabel = 60;
    public const int MaxAspect = 100;

    // Tolerancia para que x + ancho = 1 escrito con decimales no se rechace.
    private const double FractionSlack = 1e-6;

    public static SafeZonePreset ReadSafeZones(Stream stream)
    {
        using JsonDocument document = Parse(stream, SafeZonePreset.Format);
        JsonElement root = document.RootElement;
        Header(root, SafeZonePreset.Format, SafeZonePreset.Version);
        (string id, string name) = Identity(root);

        (int aspectWidth, int aspectHeight) = (0, 0);
        if (Section(root, "aspect") is { } aspect)
        {
            aspectWidth = (int)Integer(aspect, "width", "aspect.width");
            aspectHeight = (int)Integer(aspect, "height", "aspect.height");
        }

        bool center = false;
        if (root.TryGetProperty("center", out JsonElement c))
        {
            center = c.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => throw Fail("center tiene que ser true o false."),
            };
        }

        if (!root.TryGetProperty("zones", out JsonElement list) || list.ValueKind != JsonValueKind.Array)
        {
            throw Fail("Falta zones: una lista de zonas.");
        }

        int count = list.GetArrayLength();
        if (count == 0 || count > MaxSafeZones)
        {
            throw Fail($"zones tiene que traer de 1 a {MaxSafeZones} zonas.");
        }

        var zones = new List<SafeZone>(count);
        int index = 0;
        foreach (JsonElement e in list.EnumerateArray())
        {
            string where = $"zones[{index++}]";
            if (e.ValueKind != JsonValueKind.Object)
            {
                throw Fail($"{where} tiene que ser un objeto.");
            }

            string label = String(e, "label") is { } raw ? SafeText.Clean(raw) : "";
            if (label.Length == 0 || label.Length > MaxZoneLabel)
            {
                throw Fail($"{where}.label tiene que tener de 1 a {MaxZoneLabel} caracteres.");
            }

            SafeZoneKind kind = String(e, "kind") switch
            {
                null or "interface" => SafeZoneKind.Interface,
                "safe" => SafeZoneKind.Safe,
                { } other => throw Fail($"{where}.kind «{Show(other)}» no existe: interface o safe."),
            };
            (double x, double y, double w, double h) = Rect(e, where);
            zones.Add(new SafeZone(label, kind, x, y, w, h));
        }

        return new SafeZonePreset(id, name, aspectWidth, aspectHeight, zones, center);
    }

    /// <summary>El mismo JSON que se lee, para «Guardar» del editor de zonas propias.</summary>
    public static byte[] WriteSafeZones(SafeZonePreset preset, string? kind)
    {
        ArgumentNullException.ThrowIfNull(preset);
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("format", SafeZonePreset.Format);
            w.WriteNumber("version", SafeZonePreset.Version);
            if (kind is not null)
            {
                w.WriteString("kind", kind);
            }

            if (preset.Id.Length > 0)
            {
                w.WriteString("id", preset.Id);
            }

            w.WriteString("name", preset.Name);
            if (preset.AspectWidth > 0 && preset.AspectHeight > 0)
            {
                w.WriteStartObject("aspect");
                w.WriteNumber("width", preset.AspectWidth);
                w.WriteNumber("height", preset.AspectHeight);
                w.WriteEndObject();
            }

            if (preset.Center)
            {
                w.WriteBoolean("center", true);
            }

            w.WriteStartArray("zones");
            foreach (SafeZone zone in preset.Zones)
            {
                w.WriteStartObject();
                w.WriteString("label", zone.Label);
                w.WriteString("kind", zone.Kind == SafeZoneKind.Safe ? "safe" : "interface");
                w.WriteStartArray("rect");
                (double x, double width) = Rounded(zone.Left, zone.Width);
                (double y, double height) = Rounded(zone.Top, zone.Height);
                foreach (double v in (double[])[x, y, width, height])
                {
                    w.WriteNumberValue(v);
                }

                w.WriteEndArray();
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Inicio y tamaño de un eje a 4 decimales, si así se vuelven a leer: redondeados por separado pueden sumar más de 1
    /// (0,98415 + 0,01585 da 0,9842 + 0,0159) o dejar el tamaño en 0; entonces se escriben sin redondear.
    /// </summary>
    private static (double Start, double Size) Rounded(double start, double size)
    {
        double s = Math.Round(start, 4), z = Math.Round(size, 4);
        return z > 0 && s + z <= 1 + FractionSlack ? (s, z) : (start, size);
    }

    private static long Integer(JsonElement owner, string property, string where)
    {
        if (!owner.TryGetProperty(property, out JsonElement e) || e.ValueKind != JsonValueKind.Number || !e.TryGetInt64(out long value))
        {
            throw Fail($"{where} tiene que ser un entero.");
        }

        return value is >= 1 and <= MaxAspect
            ? value
            : throw Fail($"{where} queda fuera de rango (1 a {MaxAspect.ToString(CultureInfo.InvariantCulture)}).");
    }

    /// <summary><c>rect: [x, y, ancho, alto]</c> en fracciones de la imagen, dentro de ella y con tamaño.</summary>
    private static (double X, double Y, double W, double H) Rect(JsonElement zone, string where)
    {
        if (!zone.TryGetProperty("rect", out JsonElement rect) || rect.ValueKind != JsonValueKind.Array || rect.GetArrayLength() != 4)
        {
            throw Fail($"{where}.rect tiene que ser [x, y, ancho, alto].");
        }

        double[] v = new double[4];
        int i = 0;
        foreach (JsonElement n in rect.EnumerateArray())
        {
            if (n.ValueKind != JsonValueKind.Number || !double.IsFinite(n.GetDouble()))
            {
                throw Fail($"{where}.rect[{i.ToString(CultureInfo.InvariantCulture)}] tiene que ser un número.");
            }

            v[i++] = n.GetDouble();
        }

        if (v[0] < 0 || v[1] < 0 || v[2] <= 0 || v[3] <= 0 || v[0] + v[2] > 1 + FractionSlack || v[1] + v[3] > 1 + FractionSlack)
        {
            throw Fail($"{where}.rect se sale de la imagen: x e y desde 0, ancho y alto mayores que 0, y x + ancho e y + alto hasta 1.");
        }

        return (v[0], v[1], Math.Min(v[2], 1 - v[0]), Math.Min(v[3], 1 - v[1]));
    }
}
