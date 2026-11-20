using Rodaje.Media.Video;

namespace Rodaje.Media.Gpu;

/// <summary>
/// Una superficie de un decodificador que la GPU lee sin copiar: D3D11VA hoy (<see cref="D3D11TexturePayload"/>), <c>AVVkFrame</c> y
/// <c>CVPixelBuffer</c> después (D-066). Se retiene con <see cref="AddRef"/> mientras un cuadro la lee y vuelve a su pool con el último
/// <see cref="Release"/>. Los hilos de decodificación solo la entregan: no llaman a la GPU.
/// </summary>
public interface IDecoderSurface
{
    FramePixelFormat PixelFormat { get; }

    int Width { get; }

    int Height { get; }

    /// <summary>Ancho de la textura (puede medir más que la imagen: el relleno del decodificador se recorta al muestrear).</summary>
    int CodedWidth { get; }

    int CodedHeight { get; }

    VideoColor Color { get; }

    bool IsReleased { get; }

    /// <summary>Falso si ya se liberó.</summary>
    bool AddRef();

    void Release();
}
