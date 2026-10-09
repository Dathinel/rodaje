using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using FotoToma.Core.Model;

namespace FotoToma.Core.Serialization;

/// <summary>
/// Contexto del generador de código de System.Text.Json para el formato <c>.ftoma</c>.
/// </summary>
/// <remarks>
/// <para>
/// Todo el JSON del proyecto pasa por aquí, sin reflexión, para que el núcleo se pueda compilar con
/// Native AOT. Nombres en camelCase, texto indentado con saltos de línea LF, tiempos como enteros de
/// ticks, tasas como <c>{"num","den"}</c> y enumeraciones como texto en camelCase.
/// </para>
/// <para>
/// Los campos que faltan en el archivo toman el valor por defecto del modelo. Los campos desconocidos se
/// ignoran al leer y no se conservan al volver a guardar: otro programa que escriba un <c>.ftoma</c> no
/// debe guardar ahí datos propios esperando que sobrevivan a una edición en FotoToma.
/// </para>
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
    ])]
[JsonSerializable(typeof(Project))]
public sealed partial class ProjectJsonContext : JsonSerializerContext
{
    /// <summary>
    /// Opciones con las que se lee y escribe el archivo. Son las del atributo más un codificador que
    /// deja las tildes y la ñ legibles en lugar de escaparlas como <c>ñ</c>; un archivo en disco no
    /// corre el riesgo de inyección HTML para el que existe el escape estricto.
    /// </summary>
    internal static JsonSerializerOptions FileOptions { get; } = CreateFileOptions();

    /// <summary>Metadatos de <see cref="Project"/> ligados a <see cref="FileOptions"/>.</summary>
    internal static JsonTypeInfo<Project> ProjectInfo { get; } =
        (JsonTypeInfo<Project>)FileOptions.GetTypeInfo(typeof(Project));

    private static JsonSerializerOptions CreateFileOptions()
    {
        var options = new JsonSerializerOptions(Default.Options)
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
