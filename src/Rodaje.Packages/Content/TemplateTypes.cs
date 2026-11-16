using System.Text.Json.Nodes;

namespace Rodaje.Packages.Content;

/// <summary>Topes del documento y del sobre de cada capa; los del texto coinciden con los del generador de Core.</summary>
public static class TemplateLimits
{
    public const int MaxTextLength = 10_000;
    public const int MaxTextLines = 200;
    public const double MinBoxWidthFraction = double.Epsilon;
    public const double MaxPosition = 16_384;
    public const double MaxBoxWidthFraction = 1;
    public const int MaxLayers = 16;
    public const int MaxParameters = 32;
    public const int MaxTargets = 16;
    public const int MaxOptions = 16;
    public const int MaxFileBytes = 256 * 1024;
    public const double MaxSeconds = 600;
}

public enum TemplateKind { Title, Transition, Effect }

public enum TemplateParameterType { Text, Color, Font, Number, Bool, Choice }

/// <summary>Opción publicada. Su valor se entrega como copia para conservar el documento leído.</summary>
public sealed class TemplateOption
{
    private readonly JsonNode value;

    internal TemplateOption(string label, JsonNode value)
    {
        Label = label;
        this.value = value.DeepClone();
    }

    public string Label { get; }
    public JsonNode Value => value.DeepClone();
    internal JsonNode StoredValue => value;
}

public sealed record TemplateParameter(
    string Id,
    string Label,
    TemplateParameterType Type,
    IReadOnlyList<string> Targets,
    double? Min,
    double? Max,
    double? Step,
    IReadOnlyList<TemplateOption> Options);

/// <summary>Documento validado. Body devuelve una copia; los valores por defecto están en el propio cuerpo.</summary>
public sealed class TemplateDocument
{
    public const string Format = "rodaje.template";
    public const int Version = 1;
    private readonly JsonObject body;

    internal TemplateDocument(string id, string name, TemplateKind kind, int width, int height,
        double durationSeconds, double minSeconds, JsonObject body, IReadOnlyList<TemplateParameter> parameters)
    {
        Id = id;
        Name = name;
        Kind = kind;
        ReferenceWidth = width;
        ReferenceHeight = height;
        DurationSeconds = durationSeconds;
        MinSeconds = minSeconds;
        this.body = body;
        Parameters = parameters;
    }

    public string Id { get; }
    public string Name { get; }
    public TemplateKind Kind { get; }
    public int ReferenceWidth { get; }
    public int ReferenceHeight { get; }
    public double DurationSeconds { get; }
    public double MinSeconds { get; }
    public JsonObject Body => (JsonObject)body.DeepClone();
    public IReadOnlyList<TemplateParameter> Parameters { get; }
}

public sealed record TemplateBox(double WidthFraction);

/// <summary>Estilo leído por el lector existente y JSON compacto que la App puede entregar a Core.</summary>
public sealed record InstantiatedTemplateLayer(string Id, string Text, TextStylePreset Style, string StyleJson,
    double X, double Y, double Start, double End, TemplateBox? Box);

public sealed record TemplateInstance(TemplateKind Kind, IReadOnlyList<InstantiatedTemplateLayer> Layers,
    TransitionPreset? Transition, EffectPreset? Effect);
