using System.Text.Json;
using System.Text.Json.Nodes;
using Rodaje.App.Services.Titles;
using Rodaje.Core.Model;
using Rodaje.Packages.Content;

namespace Rodaje.App.Services.MotionTemplates;

/// <summary>
/// De un <c>rodaje.template</c> de títulos a lo que guarda el clip (D-064): instancia con <c>Rodaje.Packages</c> y entrega
/// a Core las capas ya hechas. Cambiar un valor y «Actualizar a X» pasan por aquí.
/// </summary>
public static class TemplateInstances
{
    /// <summary>El generador de un clip nuevo con los valores por defecto.</summary>
    /// <exception cref="PackageContentException">El documento no es una plantilla de títulos válida.</exception>
    public static ClipGenerator NewGenerator(MotionTemplateEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new ClipGenerator
        {
            Kind = GeneratorKinds.Template,
            Template = Create(entry.Document, entry.DocumentText, entry.ProjectSource, entry.Name, new Dictionary<string, JsonElement>()),
        };
    }

    /// <exception cref="PackageContentException">Un valor no corresponde a su parámetro o la plantilla no es de títulos.</exception>
    public static GeneratorTemplate Create(TemplateDocument document, string documentText, string? source, string name,
        IReadOnlyDictionary<string, JsonElement> values)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(values);
        if (document.Kind != TemplateKind.Title)
        {
            throw new PackageContentException("La plantilla no es de títulos.");
        }

        return new GeneratorTemplate
        {
            Source = source,
            Name = name,
            MinSeconds = document.MinSeconds,
            Document = documentText,
            Values = new Dictionary<string, JsonElement>(values),
            Layers = Layers(document, values, source),
        };
    }

    /// <summary>Las capas con esos valores, listas para <c>TemplateEdits.SetValues</c>.</summary>
    /// <exception cref="PackageContentException">Un valor no corresponde a su parámetro.</exception>
    public static List<TemplateLayer> Layers(TemplateDocument document, IReadOnlyDictionary<string, JsonElement> values, string? source)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(values);
        TemplateInstance instance = TemplateInstantiator.Instantiate(document, values.ToDictionary(p => p.Key, p => JsonNode.Parse(p.Value.GetRawText())));
        return [.. instance.Layers.Select(l => new TemplateLayer
        {
            Id = l.Id,
            Text = l.Text,
            Style = TitleStyles.FromPreset(l.Style, source),
            Box = l.Box is { } box ? new TextFrame { WidthFraction = box.WidthFraction } : null,
            X = l.X,
            Y = l.Y,
            Start = l.Start,
            End = l.End,
        })];
    }

    /// <summary>Los valores del clip con <paramref name="id"/> cambiado a <paramref name="value"/>.</summary>
    public static Dictionary<string, JsonElement> With(IReadOnlyDictionary<string, JsonElement> values, string id, JsonNode value)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(value);
        using JsonDocument parsed = JsonDocument.Parse(value.ToJsonString());
        return new Dictionary<string, JsonElement>(values) { [id] = parsed.RootElement.Clone() };
    }

    /// <summary>Lo que vale hoy un parámetro: el valor puesto o, si no hay, el de la plantilla.</summary>
    public static JsonNode? Current(TemplateDocument document, IReadOnlyDictionary<string, JsonElement> values, TemplateParameter parameter)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(parameter);
        return values.TryGetValue(parameter.Id, out JsonElement value) ? JsonNode.Parse(value.GetRawText()) : TemplateDefaults.Of(document, parameter);
    }

    /// <summary>
    /// «Actualizar a X»: la plantilla <paramref name="newer"/> con los valores de <paramref name="current"/> cuyo id siga
    /// existiendo y siga valiendo. <c>Reset</c> nombra los que volvieron a su defecto.
    /// </summary>
    /// <exception cref="PackageContentException">La plantilla nueva no es de títulos.</exception>
    public static (GeneratorTemplate Template, IReadOnlyList<string> Reset) Update(GeneratorTemplate current, MotionTemplateEntry newer)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(newer);
        Dictionary<string, string> oldLabels = Labels(current.Document);
        var kept = new Dictionary<string, JsonElement>();
        var reset = new List<string>();
        foreach ((string id, JsonElement value) in current.Values)
        {
            if (newer.Document.Parameters.Any(p => p.Id == id) && IsValid(newer.Document, id, value))
            {
                kept[id] = value;
            }
            else
            {
                reset.Add(oldLabels.GetValueOrDefault(id, id));
            }
        }

        return (Create(newer.Document, newer.DocumentText, newer.ProjectSource, newer.Name, kept), reset);
    }

    private static bool IsValid(TemplateDocument document, string id, JsonElement value)
    {
        try
        {
            _ = TemplateInstantiator.Instantiate(document, new Dictionary<string, JsonNode?> { [id] = JsonNode.Parse(value.GetRawText()) });
            return true;
        }
        catch (PackageContentException)
        {
            return false;
        }
    }

    private static Dictionary<string, string> Labels(string documentText)
    {
        try
        {
            return ContentReaders.ReadTemplate(documentText).Parameters.ToDictionary(p => p.Id, p => p.Label);
        }
        catch (PackageContentException)
        {
            return [];
        }
    }
}
