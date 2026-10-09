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
}

/// <summary>
/// Opciones y metadatos listos para leer y escribir el archivo. Van aparte del contexto porque el
/// orden de inicialización de los campos estáticos entre las partes de una clase parcial no está
/// definido y el contexto generado declara los suyos en otro archivo.
/// </summary>
internal static class ProjectJson
{
    /// <summary>
    /// Las opciones del atributo más un codificador que deja las tildes y la ñ legibles en lugar de
    /// escaparlas con secuencias <c>\u</c>; un archivo en disco no corre el riesgo de inyección HTML para el
    /// que existe el escape estricto.
    /// </summary>
    public static JsonSerializerOptions FileOptions { get; } = CreateFileOptions();

    /// <summary>Metadatos de <see cref="Project"/> ligados a <see cref="FileOptions"/>.</summary>
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
