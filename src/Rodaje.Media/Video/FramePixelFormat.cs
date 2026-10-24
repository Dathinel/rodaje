namespace Rodaje.Media.Video;

/// <summary>Formato de los planos de un cuadro decodificado.</summary>
public enum FramePixelFormat
{
    /// <summary>Otro formato de FFmpeg; solo en cuadros de CPU.</summary>
    Other,

    /// <summary>Y de 8 bits y UV intercalado a media resolución. En D3D11, SRV <c>R8_UNORM</c> y <c>R8G8_UNORM</c>.</summary>
    Nv12,

    /// <summary>Como NV12 con palabras de 16 bits y el dato en los 10 bits altos. SRV <c>R16_UNORM</c> y <c>R16G16_UNORM</c>.</summary>
    P010,

    /// <summary>Tres planos de 8 bits, croma a media resolución (solo en CPU).</summary>
    Yuv420P,

    /// <summary>Tres planos de 16 bits con el dato en los 10 bits bajos (solo en CPU).</summary>
    Yuv420P10,

    /// <summary>BGRA de 8 bits premultiplicado, para imágenes fijas.</summary>
    Bgra8,

    /// <summary>
    /// Como P010 con la palabra entera de 16 bits significativa: fuentes de más de 10 bits. Textura <c>P016</c> o, si la GPU
    /// no la muestrea (la RTX 3050 no), <c>P010</c>: las vistas <c>R16_UNORM</c> leen la palabra completa igual.
    /// </summary>
    P016,

    /// <summary>RGBA de 16 bits premultiplicado (<c>R16G16B16A16_UNORM</c>), para imágenes fijas de más de 8 bits.</summary>
    Rgba16,
}
