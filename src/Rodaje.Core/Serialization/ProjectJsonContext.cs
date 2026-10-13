using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Rodaje.Core.Model;

namespace Rodaje.Core.Serialization;

/// <summary>Contexto del generador de código de System.Text.Json para el formato <c>.rodaje</c>.</summary>
/// <remarks>
/// Sin reflexión, para que el núcleo compile con Native AOT. Los campos desconocidos se ignoran al leer
/// y no se conservan al guardar.
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    NewLine = "\n",
    RespectNullableAnnotations = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    Converters =
    [
        typeof(TimeJsonConverter),
        typeof(RationalJsonConverter),
        typeof(CamelCaseEnumConverter<TrackKind>),
        typeof(CamelCaseEnumConverter<Interpolation>),
        typeof(CamelCaseEnumConverter<FrameSampling>),
        typeof(CamelCaseEnumConverter<TextAlign>),
        typeof(CamelCaseEnumConverter<StrokeJoin>),
        typeof(CamelCaseEnumConverter<TextCase>),
        typeof(CamelCaseEnumConverter<TextFit>),
        typeof(CamelCaseEnumConverter<GradientKind>),
        typeof(CamelCaseEnumConverter<TextUnit>),
        typeof(CamelCaseEnumConverter<UnitOrder>),
        typeof(CamelCaseEnumConverter<HighlightMode>),
        typeof(CamelCaseEnumConverter<TextPathKind>),
        typeof(CamelCaseEnumConverter<TransitionAlignment>),

    ])]
[JsonSerializable(typeof(Project))]
public sealed partial class ProjectJsonContext : JsonSerializerContext
{
}

// Aparte del contexto: el orden de inicialización estática entre partes de una clase parcial no está definido.
internal static class ProjectJson
{
    // Codificador relajado: tildes y ñ legibles; un archivo en disco no necesita el escape anti-HTML.
    public static JsonSerializerOptions FileOptions { get; } = CreateFileOptions();

    public static JsonTypeInfo<Project> ProjectInfo { get; } =
        (JsonTypeInfo<Project>)FileOptions.GetTypeInfo(typeof(Project));

    private static JsonSerializerOptions CreateFileOptions()
    {
        var options = new JsonSerializerOptions(ProjectJsonContext.Default.Options)
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        options.MakeReadOnly();
        return options;
    }
}

/// <summary>Enumeración como texto camelCase (<c>easeInOut</c>); rechaza números al leer.</summary>
public sealed class CamelCaseEnumConverter<TEnum> : JsonStringEnumConverter<TEnum>
    where TEnum : struct, Enum
{
    public CamelCaseEnumConverter()
        : base(JsonNamingPolicy.CamelCase, allowIntegerValues: false)
    {
    }
}
