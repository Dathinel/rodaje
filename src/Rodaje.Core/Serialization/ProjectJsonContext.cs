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

    /// <summary>
    /// Las mismas opciones para abrir, más el control que rechaza un número que desborda (ver <see cref="FiniteDoubleConverter"/>).
    /// Aparte de <see cref="ProjectInfo"/>: con un convertidor propio para <see cref="double"/>, el esquema perdería el tipo «number».
    /// </summary>
    public static JsonTypeInfo<Project> ReadInfo { get; } =
        (JsonTypeInfo<Project>)CreateFileOptions(checkedReads: true).GetTypeInfo(typeof(Project));

    /// <summary>
    /// Las mismas opciones para escribir, más los controles que rechazan lo que no se puede guardar sin perderlo
    /// (números no finitos y sustitutos UTF-16 sueltos). Aparte de <see cref="ProjectInfo"/>, que también describe el esquema.
    /// </summary>
    public static JsonTypeInfo<Project> WriteInfo { get; } =
        (JsonTypeInfo<Project>)CreateFileOptions(checkedWrites: true).GetTypeInfo(typeof(Project));

    /// <summary>Como <see cref="WriteInfo"/>, pero marca lo que no se puede guardar en vez de fallar (ver <see cref="SaveCheck"/>).</summary>
    public static JsonTypeInfo<Project> LocateInfo { get; } =
        (JsonTypeInfo<Project>)CreateFileOptions(checkedWrites: true, locate: true).GetTypeInfo(typeof(Project));

    private static JsonSerializerOptions CreateFileOptions(bool checkedWrites = false, bool locate = false, bool checkedReads = false)
    {
        var options = new JsonSerializerOptions(ProjectJsonContext.Default.Options)
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        if (checkedReads)
        {
            options.Converters.Add(new FiniteDoubleConverter());
        }

        if (checkedWrites)
        {
            options.Converters.Add(new FiniteDoubleConverter(locate));
            options.Converters.Add(new WellFormedStringConverter(locate));
        }

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
