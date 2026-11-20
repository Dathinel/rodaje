namespace Rodaje.Core.Collab;

/// <summary>Límites de lo que se acepta de la red.</summary>
public sealed record WireLimits
{
    public static WireLimits Default { get; } = new();

    /// <summary>Tamaño máximo del parche en bytes UTF-8 (900 KiB: cabe con su envoltorio en el marco de 1 MiB del protocolo).</summary>
    public int MaxPatchBytes { get; init; } = 900 * 1024;

    /// <summary>Profundidad máxima de anidamiento del JSON.</summary>
    public int MaxDepth { get; init; } = 32;

    /// <summary>Cambios de elemento por parche.</summary>
    public int MaxOps { get; init; } = 20_000;

    /// <summary>Longitud máxima de un identificador.</summary>
    public int MaxIdLength { get; init; } = 128;

    /// <summary>Longitud máxima de cualquier texto suelto del JSON (nombres, notas, palabras).</summary>
    public int MaxStringLength { get; init; } = 100_000;
}

/// <summary>
/// Lista de permitidos para lo que llega por la red, antes de abrir o resolver nada: rechaza JSON de más de la profundidad o el
/// tamaño permitidos, campos desconocidos, identificadores repetidos o mal formados, y cualquier ruta (<c>file:</c>, UNC,
/// absoluta, relativa o con <c>..</c>) donde solo cabe una referencia <c>blob:</c>. No lanza nunca.
/// </summary>
public static class WireSanitizer
{
    /// <summary>Comprueba el texto de un parche; <see langword="null"/> si es aceptable.</summary>
    public static PatchRejection? Check(string json, WireLimits? limits = null) => throw Pending.Exception();
}
