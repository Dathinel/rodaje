using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Rodaje.App.Services.Inspector;
using Rodaje.App.Services.Titles;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Packages;
using Rodaje.Packages.Content;

namespace Rodaje.App.Services.MotionTemplates;

/// <summary>Lo que se puede exponer de cada capa al guardar unos títulos como plantilla.</summary>
public enum TemplateField
{
    Text,
    FillColor,
    BackgroundColor,
    StrokeColor,
    Font,
    Size,
}

/// <summary>Un campo de una capa (<see cref="Layer"/> es su posición, de abajo arriba), si se expone y con qué nombre.</summary>
public sealed record TemplateFieldChoice(int Layer, TemplateField Field, bool Exposed, string Label);

/// <summary>
/// «Guardar como plantilla…» (D-064): de uno o varios títulos a un <c>rodaje.template</c> de títulos en los complementos
/// propios. Cada título es una capa, con su posición y su desfase respecto del primero; los efectos del clip (salvo la
/// posición), el formato por palabra y los keyframes no viajan.
/// </summary>
public static class TemplateAuthoring
{
    public const double MinSize = 8;
    public const double MaxSize = 400;

    /// <summary>Tope del id de una plantilla propia: deja sitio a «-2» si el archivo ya existe y a «plantilla.» al exportarla.</summary>
    public const int MaxIdLength = 50;

    /// <summary>El tamaño más pequeño que admite el estilo: un borde no pasa de la mitad del tamaño.</summary>
    public static double MinSizeOf(TextStyle style)
    {
        ArgumentNullException.ThrowIfNull(style);
        return Math.Max(MinSize, Math.Ceiling(style.AllStrokes().Select(s => s.Width * 2).DefaultIfEmpty(0).Max()));
    }

    /// <summary>El id de archivo y de elemento de una plantilla con ese nombre: minúsculas sin tildes, sin nombres reservados y acotado.</summary>
    public static string Identifier(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        string id = PackageLibrary.ToIdentifier(name);
        return id.Length > MaxIdLength ? PackageLibrary.ToIdentifier(id[..MaxIdLength]) : id;
    }

    /// <summary>Los títulos de la selección que pueden ser capas, de abajo arriba y hasta el tope de capas.</summary>
    public static IReadOnlyList<Clip> Titles(Sequence sequence, IEnumerable<string> clipIds)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(clipIds);
        var found = new List<(int Track, Clip Clip)>();
        foreach (string id in clipIds)
        {
            if (sequence.FindClip(id) is { Clip.Generator: { Kind: GeneratorKinds.Text, Style: not null } } title)
            {
                found.Add((sequence.VideoTracks.IndexOf(title.Track), title.Clip));
            }
        }

        return [.. found.OrderBy(f => f.Track).ThenBy(f => f.Clip.Start).Select(f => f.Clip).Take(TemplateLimits.MaxLayers)];
    }

    /// <summary>Los campos que ofrece el diálogo; de entrada solo el texto va expuesto.</summary>
    public static IReadOnlyList<TemplateFieldChoice> Fields(IReadOnlyList<Clip> titles)
    {
        ArgumentNullException.ThrowIfNull(titles);
        var fields = new List<TemplateFieldChoice>();
        for (int i = 0; i < titles.Count; i++)
        {
            TextStyle style = titles[i].Generator!.Style!;
            string suffix = titles.Count > 1 ? string.Create(CultureInfo.InvariantCulture, $" {i + 1}") : "";
            fields.Add(new(i, TemplateField.Text, true, "Texto" + suffix));
            fields.Add(new(i, TemplateField.FillColor, false, "Color" + suffix));
            if (style.Background is not null)
            {
                fields.Add(new(i, TemplateField.BackgroundColor, false, "Color de fondo" + suffix));
            }

            if (style.Stroke is not null)
            {
                fields.Add(new(i, TemplateField.StrokeColor, false, "Color de borde" + suffix));
            }

            fields.Add(new(i, TemplateField.Font, false, "Fuente" + suffix));
            if (style.Size >= MinSizeOf(style) && style.Size <= MaxSize)
            {
                fields.Add(new(i, TemplateField.Size, false, "Tamaño" + suffix));
            }
        }

        return fields;
    }

    /// <summary>El documento <c>rodaje.template</c> de esos títulos con los campos expuestos.</summary>
    /// <exception cref="ArgumentException">No hay títulos, falta el nombre o un campo expuesto no tiene nombre.</exception>
    public static string Build(string name, Sequence sequence, IReadOnlyList<Clip> titles, IReadOnlyList<TemplateFieldChoice> fields)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(titles);
        ArgumentNullException.ThrowIfNull(fields);
        if (titles.Count is < 1 or > TemplateLimits.MaxLayers || titles.Any(t => t.Generator?.Style is null))
        {
            throw new ArgumentException("Hacen falta de 1 a 16 títulos.", nameof(titles));
        }

        Time start = titles.Min(t => t.Start);
        Time end = titles.Max(t => t.End);
        double duration = (end - start).ToSeconds();
        if (duration > TemplateLimits.MaxSeconds)
        {
            throw new ArgumentException($"Los títulos seleccionados abarcan más de {TemplateLimits.MaxSeconds / 60:0} minutos: una plantilla no puede durar tanto.", nameof(titles));
        }

        double minimum = 0;
        var layers = new JsonArray();
        for (int i = 0; i < titles.Count; i++)
        {
            Clip clip = titles[i];
            ClipGenerator generator = clip.Generator!;
            // Un borde con la animación desactivada no la lleva a la plantilla.
            TextStyle style = generator.Style!.Clone();
            style.In = generator.Edges.In ? style.In : null;
            style.Out = generator.Edges.Out ? style.Out : null;
            double unit = Math.Min((double)sequence.Width / style.Reference.Width, (double)sequence.Height / style.Reference.Height);
            EffectInstance? transform = clip.Effects.Find(e => e.Type == BuiltInEffects.Transform);
            double before = (clip.Start - start).ToSeconds();
            double after = (end - clip.End).ToSeconds();
            minimum = Math.Max(minimum, before + after + (style.In?.DurationSeconds ?? 0) + (style.Out?.DurationSeconds ?? 0));
            var layer = new JsonObject
            {
                ["id"] = LayerId(i),
                ["text"] = generator.Text,
                ["start"] = Round(before),
                ["end"] = Round(after),
                ["x"] = Round(Math.Clamp(Position(transform, BuiltInEffects.TransformParameters.X) / unit, -TemplateLimits.MaxPosition, TemplateLimits.MaxPosition)),
                ["y"] = Round(Math.Clamp(Position(transform, BuiltInEffects.TransformParameters.Y) / unit, -TemplateLimits.MaxPosition, TemplateLimits.MaxPosition)),
            };
            if (generator.Box?.WidthFraction is { } fraction)
            {
                layer["box"] = new JsonObject { ["widthFraction"] = fraction };
            }

            JsonObject styleJson = JsonNode.Parse(TitleStyleFile.ToJson(PackageLibrary.OwnPackageId + "." + LayerId(i), style.Name is { Length: > 0 } n ? n : LayerId(i), style))!.AsObject();
            styleJson.Remove("id");
            styleJson.Remove("kind");
            layer["style"] = styleJson;
            layers.Add((JsonNode)layer);
        }

        var parameters = new JsonArray();
        foreach (TemplateFieldChoice field in fields.Where(f => f.Exposed))
        {
            if (field.Layer < 0 || field.Layer >= titles.Count || string.IsNullOrWhiteSpace(field.Label))
            {
                throw new ArgumentException("Cada campo expuesto necesita un nombre.", nameof(fields));
            }

            if (parameters.Count == TemplateLimits.MaxParameters)
            {
                throw new ArgumentException($"Una plantilla admite hasta {TemplateLimits.MaxParameters} campos expuestos.", nameof(fields));
            }

            (string suffix, string type, string path) = field.Field switch
            {
                TemplateField.Text => ("texto", "text", "text"),
                TemplateField.FillColor => ("color", "color", "style/fill/color"),
                TemplateField.BackgroundColor => ("fondo", "color", "style/background/color"),
                TemplateField.StrokeColor => ("borde", "color", "style/stroke/color"),
                TemplateField.Font => ("fuente", "font", "style/font"),
                _ => ("tamano", "number", "style/size"),
            };
            var parameter = new JsonObject
            {
                ["id"] = $"{LayerId(field.Layer)}-{suffix}",
                ["label"] = field.Label.Trim(),
                ["type"] = type,
            };
            if (field.Field == TemplateField.Size)
            {
                parameter["min"] = MinSizeOf(titles[field.Layer].Generator!.Style!);
                parameter["max"] = MaxSize;
                parameter["step"] = 1;
            }

            parameter["targets"] = new JsonArray($"layers/{LayerId(field.Layer)}/{path}");
            parameters.Add((JsonNode)parameter);
        }

        TextStyle first = titles[0].Generator!.Style!;
        var root = new JsonObject
        {
            ["format"] = TemplateDocument.Format,
            ["version"] = TemplateDocument.Version,
            ["name"] = name.Trim(),
            ["kind"] = "title",
            ["reference"] = new JsonObject { ["width"] = first.Reference.Width, ["height"] = first.Reference.Height },
            ["durationSeconds"] = Round(duration),
            ["minSeconds"] = Round(Math.Min(minimum, duration)),
            ["body"] = new JsonObject { ["layers"] = layers },
            ["parameters"] = parameters,
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    /// <summary>Guarda el documento en <paramref name="folder"/> (los complementos propios) sin pisar otro del mismo nombre.</summary>
    /// <returns>La ruta y la referencia <c>propios@0.0.0/&lt;id&gt;</c>.</returns>
    public static (string Path, string Reference) Save(string folder, string name, string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(json);
        Directory.CreateDirectory(folder);
        string slug = Identifier(name);
        string path = Path.Combine(folder, slug + AnimationPreset.FileExtension);
        for (int n = 2; File.Exists(path); n++)
        {
            path = Path.Combine(folder, $"{slug}-{n.ToString(CultureInfo.InvariantCulture)}{AnimationPreset.FileExtension}");
        }

        AppDataPaths.WriteAllTextAtomic(path, json);
        return (path, $"{PackageLibrary.OwnPackageId}@0.0.0/{Path.GetFileNameWithoutExtension(path)}");
    }

    /// <summary>«Exportar…»: el <c>.paquete</c> de un elemento con esa plantilla, listo para instalar en otro equipo.</summary>
    public static byte[] Export(MotionTemplateEntry entry, string author)
    {
        ArgumentNullException.ThrowIfNull(entry);
        string id = PackageReference.TryParse(entry.Reference, out PackageReference? reference) ? reference.ItemId : Identifier(entry.Name);
        string packageId = "plantilla." + id;
        if (packageId.Length > Identifiers.MaxLength)
        {
            packageId = packageId[..Identifiers.MaxLength].TrimEnd('-', '.');
        }

        var package = new SingleItemPackage(packageId, PackageVersion.Parse("1.0.0"), entry.Name,
            string.IsNullOrWhiteSpace(author) ? "Sin autor" : author.Trim(), "LicenseRef-Propio", "rodaje", id, ItemKind.MotionTemplate, entry.Name,
            $"plantillas/{id}.json");
        return PackageWriter.Write(package, System.Text.Encoding.UTF8.GetBytes(entry.DocumentText));
    }

    private static string LayerId(int index) => string.Create(CultureInfo.InvariantCulture, $"capa-{index + 1}");

    private static double Position(EffectInstance? transform, string name) =>
        transform is not null && transform.Parameters.TryGetValue(name, out Parameter? parameter) ? parameter.Value : 0;

    private static double Round(double value) => Math.Round(value, 4);
}
