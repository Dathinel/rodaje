namespace Rodaje.Core.Interchange;

/// <summary>
/// Presupuestos al leer un archivo ajeno, que no es de confianza. <see cref="Model.TimelineLimits"/> limita tiempos y
/// tasas; esto limita cantidades.
/// </summary>
public sealed record InterchangeLimits(long MaxFileBytes, int MaxObjects, int MaxTextLength, int MaxDepth, int MaxFlattenedClips)
{
    public static InterchangeLimits Default { get; } = new(256L << 20, 200_000, 1 << 20, 32, 200_000);
}

/// <summary>El archivo no se puede leer: está roto o pasa de un presupuesto. El mensaje se muestra tal cual.</summary>
public sealed class InterchangeException(string message, Exception? inner = null) : Exception(message, inner);
