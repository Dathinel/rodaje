using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Rodaje.App.Services.Inspector;
using Rodaje.Core.Model;
using Rodaje.Paquetes;

namespace Rodaje.App.Services.Titles;

/// <summary>
/// «Guardar como estilo»: la copia del estilo de un título como <c>rodaje.textstyle</c> v1 en los complementos propios,
/// con <c>kind</c> para que la biblioteca lo encuentre suelto y un <c>.paquete</c> pueda llevarlo tal cual.
/// </summary>
public static class TitleStyleFile
{
    public const string Kind = "text-style";

    /// <summary>Guarda el estilo en <paramref name="folder"/> con un nombre de archivo libre.</summary>
    /// <returns>La ruta y la referencia <c>propios@0.0.0/&lt;id&gt;</c> con la que la biblioteca lo ofrece.</returns>
    public static (string Path, string Reference) Save(string folder, string name, TextStyle style)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(style);
        Directory.CreateDirectory(folder);
        string slug = AnimationPreset.Slug(name);
        string path = Path.Combine(folder, slug + AnimationPreset.FileExtension);
        for (int n = 2; File.Exists(path); n++)
        {
            path = Path.Combine(folder, $"{slug}-{n.ToString(CultureInfo.InvariantCulture)}{AnimationPreset.FileExtension}");
        }

        string id = Path.GetFileNameWithoutExtension(path);
        AppDataPaths.WriteAllTextAtomic(path, ToJson(PackageLibrary.OwnPackageId + "." + id, name, style));
        return (path, $"{PackageLibrary.OwnPackageId}@0.0.0/{id}");
    }

    public static string ToJson(string id, string name, TextStyle style)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(style);
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            json.WriteStartObject();
            json.WriteString("format", "rodaje.textstyle");
            json.WriteNumber("version", 1);
            json.WriteString("id", id);
            json.WriteString("kind", Kind);
            json.WriteString("name", name.Trim());
            json.WriteStartObject("font");
            json.WriteString("family", style.Font.Family);
            json.WriteNumber("weight", style.Font.Weight);
            json.WriteBoolean("italic", style.Font.Italic);
            if (style.Font.Source is { } source)
            {
                json.WriteString("source", source);
            }

            json.WriteStartArray("fallback");
            foreach (string family in style.Font.Fallback)
            {
                json.WriteStringValue(family);
            }

            json.WriteEndArray();
            json.WriteEndObject();
            json.WriteNumber("size", style.Size);
            json.WriteStartObject("reference");
            json.WriteNumber("width", style.Reference.Width);
            json.WriteNumber("height", style.Reference.Height);
            json.WriteEndObject();
            json.WriteNumber("lineHeight", style.LineHeight);
            json.WriteNumber("letterSpacing", style.LetterSpacing);
            json.WriteString("align", style.Align switch { TextAlign.Left => "left", TextAlign.Right => "right", _ => "center" });
            json.WriteStartObject("fill");
            json.WriteString("color", style.Fill.Color);
            json.WriteEndObject();
            if (style.Stroke is { } stroke)
            {
                json.WriteStartObject("stroke");
                json.WriteString("color", stroke.Color);
                json.WriteNumber("width", stroke.Width);
                json.WriteString("join", stroke.Join switch { StrokeJoin.Miter => "miter", StrokeJoin.Bevel => "bevel", _ => "round" });
                json.WriteEndObject();
            }

            if (style.Shadow is { } shadow)
            {
                json.WriteStartObject("shadow");
                json.WriteString("color", shadow.Color);
                json.WriteNumber("dx", shadow.Dx);
                json.WriteNumber("dy", shadow.Dy);
                json.WriteNumber("blur", shadow.Blur);
                json.WriteEndObject();
            }

            if (style.Background is { } background)
            {
                json.WriteStartObject("background");
                json.WriteString("color", background.Color);
                json.WriteStartArray("padding");
                foreach (double padding in background.Padding)
                {
                    json.WriteNumberValue(padding);
                }

                json.WriteEndArray();
                json.WriteNumber("radius", background.Radius);
                json.WriteEndObject();
            }

            Animation(json, "in", style.In);
            Animation(json, "out", style.Out);
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray()) + "\n";
    }

    private static void Animation(Utf8JsonWriter json, string property, StyleAnimation? animation)
    {
        if (animation is not { DurationSeconds: > 0 })
        {
            return;
        }

        json.WriteStartObject(property);
        json.WriteNumber("durationSeconds", animation.DurationSeconds);
        json.WriteStartObject("curves");
        foreach ((string parameter, List<StyleKey> keys) in animation.Curves)
        {
            json.WriteStartArray(parameter);
            foreach (StyleKey key in keys)
            {
                json.WriteStartObject();
                json.WriteNumber("t", key.T);
                json.WriteNumber("value", key.Value);
                json.WriteString("interp", AnimationPreset.InterpolationName(key.Interp));
                json.WriteEndObject();
            }

            json.WriteEndArray();
        }

        json.WriteEndObject();
        json.WriteEndObject();
    }
}
