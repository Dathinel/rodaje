namespace Rodaje.Paquetes.Content;

/// <summary>
/// Lectores de los archivos de contenido de un paquete. Validan con <c>JsonDocument</c> (apto para AOT), nunca
/// confían en el archivo y lanzan <see cref="PackageContentException"/> con el motivo.
/// </summary>
/// <remarks>TODO(fase 5, agente D): implementar los cuatro lectores con sus pruebas (fuzz de <c>.cube</c>).</remarks>
public static class ContentReaders
{
    /// <summary>Lee un <c>.cube</c> 3D de hasta <see cref="LutTable.MaxFileBytes"/>; 65³ en 150 ms o menos.</summary>
    public static LutTable ReadCube(Stream stream) => throw Pending(stream);

    public static FilterPreset ReadFilter(Stream stream) => throw Pending(stream);

    public static EffectPreset ReadEffect(Stream stream) => throw Pending(stream);

    /// <summary>Lee <c>rodaje.preset</c> v2 y también v1.</summary>
    public static TransitionPreset ReadTransition(Stream stream) => throw Pending(stream);

    private static NotImplementedException Pending(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return new NotImplementedException("Lector de contenido pendiente (fase 5).");
    }
}
