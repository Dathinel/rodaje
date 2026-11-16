using System.Text.Json.Nodes;

namespace Rodaje.Packages.Content;

/// <summary>El valor por defecto de un parámetro: lo que el cuerpo de la plantilla trae en su primer destino.</summary>
public static class TemplateDefaults
{
    public static JsonNode? Of(TemplateDocument document, TemplateParameter parameter)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(parameter);
        (JsonNode parent, string segment) = PresetReader.ResolveTemplateTarget(document.Body, parameter.Targets[0]);
        return (parent is JsonObject obj ? obj[segment] : parent.AsArray()[int.Parse(segment, System.Globalization.CultureInfo.InvariantCulture)])?.DeepClone();
    }
}
