namespace Rodaje.Core.Collab;

/// <summary>
/// Un cambio de un elemento: su contenido nuevo (JSON de red, sin rutas) o <see langword="null"/> si se borra.
/// <see cref="BaseVersion"/> es la versión del elemento de la que partió quien lo hizo; el anfitrión solo lo acepta si
/// coincide. Un elemento nuevo parte de la versión que <see cref="ElementVersions"/> conozca para ese identificador
/// (0 si nunca existió).
/// </summary>
/// <param name="Kind">Clase del elemento.</param>
/// <param name="Id">Identificador del elemento.</param>
/// <param name="Parent">Secuencia (pista, marcador, región) o pista (clip) que lo contiene; <see langword="null"/> en los demás.</param>
/// <param name="BaseVersion">Versión de la que partió.</param>
/// <param name="Content">JSON canónico del elemento, o <see langword="null"/> para borrarlo.</param>
public sealed record PatchOp(ElementKind Kind, string Id, string? Parent, int BaseVersion, string? Content)
{
    public ElementKey Key => new(Kind, Id);

    public bool IsDelete => Content is null;
}

/// <summary>
/// El orden de una lista (medios, secuencias, pistas de una secuencia…) cuando no sale de aplicar el resto del parche:
/// los elementos nuevos van al final, los borrados se quitan y los que no aparecen en <see cref="Ids"/> conservan su
/// sitio al final. Las claves de contenedor están en <see cref="PatchContainers"/>.
/// </summary>
/// <param name="Container">La lista ordenada («media», «atracks:sec1»…), con las claves de <see cref="PatchContainers"/>.</param>
/// <param name="Ids">Los identificadores en el orden nuevo; los que falten conservan su sitio al final.</param>
/// <param name="BaseVersion">Versión del contenedor de la que partió quien lo hizo (en <see cref="ElementVersions"/>); el
/// anfitrión solo lo acepta si coincide, igual que con <see cref="PatchOp.BaseVersion"/>: reordenar sobre un orden ya
/// cambiado es un conflicto, no una mezcla.</param>
public sealed record PatchOrder(string Container, IReadOnlyList<string> Ids, int BaseVersion = 0);

/// <summary>Un archivo que el parche necesita y que viaja aparte (LUT, máscara, análisis): se pide por su huella.</summary>
public sealed record PatchBlob(string Sha256, long Size);

/// <summary>Encabezado de un parche que se va a crear.</summary>
/// <param name="Id">Identificador propio (un GUID en texto); el anfitrión descarta los repetidos, así reenviar es seguro.</param>
/// <param name="BaseOrder">Número de orden confirmado sobre el que se hizo.</param>
/// <param name="Author">Clave del autor (la pone la sala; Core no la interpreta).</param>
/// <param name="Description">Texto corto del paso («Recortar en cadena»).</param>
public sealed record PatchHeader(string Id, long BaseOrder, string Author, string Description);

/// <summary>
/// Un cambio atómico del proyecto compartido: se acepta entero o se rechaza entero. Lo crea
/// <see cref="PatchDiff"/>, se manda como JSON (<see cref="PatchWire"/>) y lo aplica <see cref="PatchApply"/>.
/// </summary>
public sealed record Patch(
    string Id,
    long BaseOrder,
    string Author,
    string Description,
    IReadOnlyList<PatchOp> Ops,
    IReadOnlyList<PatchOrder> Orders,
    IReadOnlyList<PatchBlob> Blobs)
{
    /// <summary>Un parche sin cambios no se manda.</summary>
    public bool IsEmpty => Ops.Count == 0 && Orders.Count == 0;
}

/// <summary>Nombres de las listas ordenadas del proyecto, para <see cref="PatchOrder.Container"/>.</summary>
public static class PatchContainers
{
    public const string Media = "media";
    public const string Sequences = "sequences";
    public const string Transcripts = "transcripts";

    public static string VideoTracks(string sequenceId) => "vtracks:" + sequenceId;

    public static string AudioTracks(string sequenceId) => "atracks:" + sequenceId;

    public static string Markers(string sequenceId) => "markers:" + sequenceId;

    public static string Regions(string sequenceId) => "regions:" + sequenceId;
}
