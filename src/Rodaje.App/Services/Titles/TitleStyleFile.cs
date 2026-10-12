using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Rodaje.App.Services.Inspector;
using Rodaje.Core.Model;
using Rodaje.Packages;

namespace Rodaje.App.Services.Titles;

/// <summary>
/// «Guardar como estilo»: la copia del estilo de un título (con su animación) como <c>rodaje.textstyle</c> en los complementos propios,
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
            json.WriteNumber("version", NeedsVersion2(style) ? 2 : 1);
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
            if (style.Fill.Gradient is { } gradient)
            {
                json.WriteStartObject("gradient");
                json.WriteString("kind", gradient.Kind == GradientKind.Radial ? "radial" : "linear");
                json.WriteNumber("angle", gradient.Angle);
                json.WriteStartArray("stops");
                foreach (ColorStop stop in gradient.Stops)
                {
                    json.WriteStartObject();
                    json.WriteNumber("position", stop.Position);
                    json.WriteString("color", stop.Color);
                    json.WriteEndObject();
                }

                json.WriteEndArray();
                json.WriteEndObject();
            }

            json.WriteEndObject();
            if (style.Stroke is { } stroke)
            {
                json.WritePropertyName("stroke");
                Stroke(json, stroke);
            }

            if (style.Shadow is { } shadow)
            {
                json.WritePropertyName("shadow");
                Shadow(json, shadow);
            }

            if (style.ExtraStrokes is { Count: > 0 } strokes)
            {
                json.WriteStartArray("extraStrokes");
                strokes.ForEach(s => Stroke(json, s));
                json.WriteEndArray();
            }

            if (style.ExtraShadows is { Count: > 0 } shadows)
            {
                json.WriteStartArray("extraShadows");
                shadows.ForEach(s => Shadow(json, s));
                json.WriteEndArray();
            }

            if (style.Typography is { } typography)
            {
                json.WriteStartObject("typography");
                json.WriteBoolean("kerning", typography.Kerning);
                json.WriteString("case", typography.Case switch
                {
                    TextCase.Upper => "upper",
                    TextCase.Lower => "lower",
                    TextCase.SmallCaps => "smallCaps",
                    _ => "none",
                });
                json.WriteStartArray("features");
                typography.Features.ForEach(json.WriteStringValue);
                json.WriteEndArray();
                json.WriteString("fit", typography.Fit switch { TextFit.Shrink => "shrink", TextFit.Fill => "fill", _ => "none" });
                json.WriteEndObject();
            }

            if (style.Path is { } path)
            {
                json.WriteStartObject("path");
                json.WriteString("kind", "arc");
                json.WriteNumber("bend", path.Bend);
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

            if (style.Highlight is { } highlight)
            {
                json.WriteStartObject("highlight");
                json.WriteString("color", highlight.Color);
                if (highlight.Mode == HighlightMode.Sweep)
                {
                    json.WriteString("mode", "sweep");
                }

                json.WriteEndObject();
            }

            Animation(json, "in", style.In);
            Animation(json, "out", style.Out);
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray()) + "\n";
    }

    /// <summary>Lo que solo entiende la versión 2 de <c>rodaje.textstyle</c>; sin nada de eso se escribe la 1, que leen las versiones anteriores.</summary>
    public static bool NeedsVersion2(TextStyle style)
    {
        ArgumentNullException.ThrowIfNull(style);
        return style.Fill.Gradient is not null || style.ExtraStrokes is { Count: > 0 } || style.ExtraShadows is { Count: > 0 }
            || style.Typography is not null || style.Path is not null || style.Highlight?.Mode == HighlightMode.Sweep
            || style.In?.PerUnit == true || style.Out?.PerUnit == true
            || new[] { style.In, style.Out }.Any(a => a?.Curves.Values.Any(k => k.Any(p => p.Interp is Interpolation.Bounce or Interpolation.Elastic)) == true);
    }

    private static void Stroke(Utf8JsonWriter json, StyleStroke stroke)
    {
        json.WriteStartObject();
        json.WriteString("color", stroke.Color);
        json.WriteNumber("width", stroke.Width);
        json.WriteString("join", stroke.Join switch { StrokeJoin.Miter => "miter", StrokeJoin.Bevel => "bevel", _ => "round" });
        json.WriteEndObject();
    }

    private static void Shadow(Utf8JsonWriter json, StyleShadow shadow)
    {
        json.WriteStartObject();
        json.WriteString("color", shadow.Color);
        json.WriteNumber("dx", shadow.Dx);
        json.WriteNumber("dy", shadow.Dy);
        json.WriteNumber("blur", shadow.Blur);
        json.WriteEndObject();
    }

    private static void Animation(Utf8JsonWriter json, string property, StyleAnimation? animation)

    {
        if (animation is not { DurationSeconds: > 0 })
        {
            return;
        }

        json.WriteStartObject(property);
        json.WriteNumber("durationSeconds", animation.DurationSeconds);
        if (animation.Unit != TextUnit.Block)
        {
            json.WriteString("unit", animation.Unit switch { TextUnit.Line => "line", TextUnit.Word => "word", _ => "letter" });
        }

        if (animation.Stagger > 0)
        {
            json.WriteNumber("stagger", animation.Stagger);
        }

        if (animation.Order != UnitOrder.Forward)
        {
            json.WriteString("order", animation.Order switch
            {
                UnitOrder.Backward => "backward",
                UnitOrder.Center => "center",
                UnitOrder.Edges => "edges",
                _ => "random",
            });
        }

        json.WriteStartObject("curves");
        foreach ((string parameter, List<StyleKey> keys) in animation.Curves)
        {
            json.WriteStartArray(parameter);
            foreach (StyleKey key in keys)
            {
                json.WriteStartObject();
                json.WriteNumber("t", key.T);
                json.WriteNumber("value", key.Value);
                json.WriteString("interp", AnimationPreset.InterpolationName(key.Interp == Interpolation.Bezier ? Interpolation.EaseInOut : key.Interp));
                json.WriteEndObject();
            }

            json.WriteEndArray();
        }

        json.WriteEndObject();
        json.WriteEndObject();
    }
}
