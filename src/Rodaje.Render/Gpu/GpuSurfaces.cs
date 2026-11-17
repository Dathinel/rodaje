namespace Rodaje.Render.Gpu;

/// <summary>Los planos de una superficie del decodificador, listos para muestrear (t0 luma, t1 croma; en RGBA, los dos son la imagen).</summary>
public readonly record struct GpuDecoderPlanes(GpuTexture Luma, GpuTexture Chroma);

/// <summary>
/// Las vistas de las superficies del decodificador que un dueño (un compositor) reutiliza cuadro tras cuadro. Retienen el pool de su
/// decodificador: se sueltan con <see cref="Trim"/> (pools cerrados y vistas sin uso) o <see cref="Clear"/>. Se usa dentro de un cuadro.
/// </summary>
public abstract class GpuSurfaceCache : GpuResource
{
    public abstract int Count { get; }

    /// <summary>Cierra un cuadro: cada tanto, o si se cerró un decodificador, hace <see cref="Trim"/>.</summary>
    public abstract void EndFrame();

    public abstract void Trim();

    public abstract void Clear();
}

/// <summary>
/// Una cadena de intercambio: la crea el motor para su ventana (<c>SwapChainPanel</c>, X11, <c>CAMetalLayer</c>). Regla 6 de
/// <c>docs/igpu.md</c>: presenta tras el cuadro que dibujó el búfer trasero.
/// </summary>
public interface IGpuSwapchain : IDisposable
{
    int Width { get; }

    int Height { get; }

    GpuFormat Format { get; }

    /// <summary>El búfer trasero de este cuadro, como destino de color; vale hasta <see cref="Present"/> o <see cref="Resize"/>.</summary>
    GpuTexture AcquireBackBuffer(IGpuFrame frame);

    /// <summary>Fuera de un cuadro y sin búferes traseros en la mano.</summary>
    void Resize(int width, int height);

    /// <summary>Presenta lo dibujado; falso si la cadena no lo aceptó ahora (ocupada u oculta) y el cuadro se pierde.</summary>
    bool Present(bool vsync);
}
