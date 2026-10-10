namespace Rodaje.Media.Video;

/// <summary>El pool de D3D11VA que un decodificador está por crear.</summary>
/// <param name="Width">Ancho de cada superficie, con la alineación del decodificador.</param>
/// <param name="Height">Alto de cada superficie.</param>
/// <param name="SurfaceBytes">Bytes de una superficie (NV12 o P010).</param>
/// <param name="ReferenceSurfaces">Superficies que el códec necesita para sus referencias y su salida.</param>
public readonly record struct HardwarePoolRequest(int Width, int Height, long SurfaceBytes, int ReferenceSurfaces);
