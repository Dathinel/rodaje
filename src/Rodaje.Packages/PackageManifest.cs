namespace Rodaje.Packages;

/// <summary>Qué es un paquete: contenido (datos, nunca código) o un módulo de IA (un proceso aparte).</summary>
public enum PackageKind
{
    Content,
    Module,
}

/// <summary>Tipos de elemento que puede traer un paquete de contenido.</summary>
public enum ItemKind
{
    Transition,
    TextStyle,
    Effect,
    Filter,
    Lut,
    Template,
    Sound,

    /// <summary>Una familia tipográfica: sus archivos <c>.ttf</c>/<c>.otf</c> sin modificar, con su licencia (D-029).</summary>
    Font,
}

/// <summary>
/// Un elemento de un paquete: una transición, un estilo de texto, un efecto, un filtro, una LUT, una plantilla, un sonido o
/// una fuente. <see cref="Id"/> es único dentro del paquete y forma parte de la referencia estable
/// <c>paquete@versión/elemento</c>; <see cref="File"/> es relativa a la carpeta del paquete, con <c>/</c>;
/// <see cref="License"/> se da cuando no es la del paquete (por ejemplo, un sonido CC BY) y es obligatoria en una fuente.
/// <see cref="Files"/> son los archivos de una fuente (uno por peso y cursiva, o uno variable).
/// </summary>
public sealed record PackageItem(
    string Id,
    ItemKind Kind,
    string Name,
    string? File = null,
    string? Description = null,
    string? License = null,
    string? Preview = null,
    IReadOnlyList<string>? Files = null)
{
    /// <summary>Tope de un archivo de fuente (D-029).</summary>
    public const long MaxFontFileBytes = 20L * 1024 * 1024;

    /// <summary>Tope de una familia, sumando sus archivos (D-029).</summary>
    public const long MaxFontFamilyBytes = 100L * 1024 * 1024;

    public const int MaxFontFiles = 32;

    /// <summary>Extensiones de un archivo de fuente.</summary>
    public static IReadOnlyList<string> FontExtensions { get; } = [".ttf", ".otf", ".ttc", ".otc"];
}

/// <summary>
/// Cómo arranca un módulo de IA: su ejecutable relativo a la carpeta del módulo, la versión del protocolo de módulos
/// (tubería con JSON) que habla y lo que ofrece (por ejemplo <c>subtitulos</c>). En la versión 0 no se ejecuta nada.
/// </summary>
public sealed record ModuleInfo(string Entry, int Protocol, IReadOnlyList<string> Functions);

/// <summary>El <c>paquete.json</c> ya leído y validado.</summary>
public sealed record PackageManifest
{
    /// <summary>Versión del formato del manifiesto que entiende este código.</summary>
    public const int CurrentFormatVersion = 1;

    public const string FileName = "paquete.json";

    public const string FileExtension = ".paquete";

    public required int FormatVersion { get; init; }

    public required string Id { get; init; }

    public required PackageVersion Version { get; init; }

    public required PackageKind Kind { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    public required string Author { get; init; }

    /// <summary>Identificador SPDX (por ejemplo <c>MIT</c> o <c>CC-BY-4.0</c>).</summary>
    public required string License { get; init; }

    /// <summary>Repositorio de origen (https).</summary>
    public Uri? Repository { get; init; }

    /// <summary>Programas en los que funciona, por su identificador (por ejemplo <c>rodaje</c>).</summary>
    public required IReadOnlyList<string> Apps { get; init; }

    public IReadOnlyList<PackageItem> Items { get; init; } = [];

    /// <summary>Solo en los módulos.</summary>
    public ModuleInfo? Module { get; init; }

    public bool WorksIn(string appId) => Apps.Contains(appId, StringComparer.OrdinalIgnoreCase);

    /// <summary>Referencia estable de un elemento: <c>id@versión/elemento</c>.</summary>
    public PackageReference ReferenceTo(PackageItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return new PackageReference(Id, Version, item.Id);
    }
}
