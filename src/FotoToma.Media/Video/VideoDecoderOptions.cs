using FotoToma.Media.Gpu;

namespace FotoToma.Media.Video;

/// <summary>Opciones de <see cref="VideoDecoder"/>.</summary>
public sealed record VideoDecoderOptions
{
    /// <summary>
    /// Dispositivo para D3D11VA y para subir los cuadros de software a texturas. Sin dispositivo se decodifica
    /// en la CPU y el contenido de cada cuadro es un <see cref="CpuFramePayload"/>.
    /// </summary>
    public GpuDevice? Device { get; init; }

    /// <summary>Permite D3D11VA cuando el dispositivo lo admite; si es <c>false</c> se decodifica en la CPU y se sube.</summary>
    public bool AllowHardwareDecoding { get; init; } = true;

    /// <summary>
    /// Texturas extra en el pool de D3D11VA, sobre las que el códec necesita como referencias. Cada cuadro que
    /// alguien retiene (caché, pantalla) ocupa una; si se agotan, el decodificador falla.
    /// </summary>
    public int ExtraHardwareFrames { get; init; } = 16;

    /// <summary>Hasta cuántos cuadros hacia delante se decodifica sin buscar (seek); más lejos se busca el keyframe.</summary>
    public int MaxForwardDecodeFrames { get; init; } = 64;

    /// <summary>Hilos de la decodificación por software; 0 elige según la CPU.</summary>
    public int SoftwareThreads { get; init; }
}
