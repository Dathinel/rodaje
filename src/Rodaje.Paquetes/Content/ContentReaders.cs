namespace Rodaje.Paquetes.Content;

/// <summary>
/// Lectores de los archivos de contenido de un paquete. Validan con <c>JsonDocument</c> (apto para AOT), nunca
/// confían en el archivo y lanzan <see cref="PackageContentException"/> con el motivo.
/// </summary>
/// <remarks>Formatos y reglas en <c>docs/formato-paquete.md</c>.</remarks>
public static class ContentReaders
{
    /// <summary>Lee un <c>.cube</c> 3D de hasta <see cref="LutTable.MaxFileBytes"/>; 65³ en 150 ms o menos.</summary>
    public static LutTable ReadCube(Stream stream) => CubeReader.Read(stream);

    public static FilterPreset ReadFilter(Stream stream) => PresetReader.ReadFilter(stream);

    public static EffectPreset ReadEffect(Stream stream) => PresetReader.ReadEffect(stream);

    /// <summary>Lee <c>rodaje.preset</c> v2 y también v1.</summary>
    public static TransitionPreset ReadTransition(Stream stream) => PresetReader.ReadTransition(stream);
}
