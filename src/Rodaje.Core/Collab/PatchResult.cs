namespace Rodaje.Core.Collab;

/// <summary>Por qué se rechaza un parche.</summary>
public enum PatchRejectionReason
{
    /// <summary>El texto no es un parche: JSON roto, campos que faltan o con otro tipo, identificadores repetidos.</summary>
    Malformed,

    /// <summary>Pasa el formato pero trae algo que la red no puede llevar: rutas, referencias <c>file:</c>, campos desconocidos.</summary>
    Hostile,

    /// <summary>Supera un límite de tamaño, profundidad o cantidad.</summary>
    TooLarge,

    /// <summary>Alguien cambió el elemento después de la versión de la que partió el parche.</summary>
    VersionConflict,

    /// <summary>El elemento cuelga de una pista o secuencia que no existe.</summary>
    UnknownParent,

    /// <summary>Con el parche el proyecto ya no sería válido (<c>ModelValidator</c>).</summary>
    Invalid,
}

/// <summary>El porqué de un rechazo, con un texto en español para quien lo vea.</summary>
/// <param name="Reason">Clase de rechazo.</param>
/// <param name="Message">Qué pasó y qué hacer; sin rutas del equipo ni datos personales.</param>
/// <param name="Element">El elemento que lo causó, si hay uno.</param>
public sealed record PatchRejection(PatchRejectionReason Reason, string Message, ElementKey? Element = null);

/// <summary>Resultado de aplicar un parche: aceptado (el proyecto y las versiones cambiaron) o rechazado (nada cambió).</summary>
public sealed record PatchResult(PatchRejection? Rejection)
{
    public static PatchResult Accepted { get; } = new((PatchRejection?)null);

    public bool IsAccepted => Rejection is null;

    public static PatchResult Reject(PatchRejectionReason reason, string message, ElementKey? element = null) =>
        new(new PatchRejection(reason, message, element));
}
