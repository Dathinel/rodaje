using Rodaje.Media.Gpu;

namespace Rodaje.Media.Video;

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

    /// <summary>
    /// Si se da, decide las texturas extra al crear el pool, cuando ya se conocen el tamaño de cada superficie y las
    /// que pide el códec; reemplaza a <see cref="ExtraHardwareFrames"/>. Se llama en el hilo que decodifica.
    /// </summary>
    public Func<HardwarePoolRequest, int>? HardwarePoolSizer { get; init; }

    /// <summary>
    /// Reserva para referencias lo que permite el nivel del flujo (H.264 y HEVC) en lugar del peor caso de FFmpeg, que
    /// supone 16 referencias a cualquier resolución: en 4K son 6 como mucho y cada superficie pesa unos 12 MB.
    /// </summary>
    public bool CompactReferencePool { get; init; }

    /// <summary>Hasta cuántos cuadros hacia delante se decodifica sin buscar (seek); más lejos se busca el keyframe.</summary>
    public int MaxForwardDecodeFrames { get; init; } = 64;

    /// <summary>Hilos de la decodificación por software; 0 elige según la CPU.</summary>
    public int SoftwareThreads { get; init; }

    /// <summary>
    /// Permite las peticiones aproximadas de alta velocidad (<see cref="DecodeAccuracy"/>): sin los cuadros que no son
    /// referencia (<c>AVDISCARD_NONREF</c>) o solo keyframes. Sin ella toda petición es exacta, como debe ser al exportar.
    /// Las peticiones exactas (pausa, seek, <c>IVideoFrameSource</c>) nunca descartan nada, con o sin esta opción.
    /// </summary>
    public bool SkipNonReference { get; init; }
}
