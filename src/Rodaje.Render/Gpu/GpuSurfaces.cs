using Rodaje.Media.Gpu;
using Rodaje.Media.Video;

namespace Rodaje.Render.Gpu;

/// <summary>Los planos de una superficie del decodificador, listos para muestrear (t0 luma, t1 croma; en RGBA, los dos son la imagen).</summary>
public readonly record struct GpuDecoderPlanes(GpuTexture Luma, GpuTexture Chroma);

/// <summary>
/// Una superficie hecha de texturas de un <see cref="IGpu"/>: los planos que sube la CPU (decodificación sin GPU, imágenes) o que
/// genera la GPU, en cualquier motor. NV12/P010 son dos texturas (<see cref="GpuFormat.R8Unorm"/>/<see cref="GpuFormat.Rg8Unorm"/> o
/// las de 16 bits); RGBA es una. Empieza con una referencia; con el último <see cref="Release"/> suelta las texturas si son suyas.
/// </summary>
public sealed class GpuTextureSurface : IDecoderSurface, IDisposable
{
    private readonly bool _ownsTextures;
    private int _references = 1;

    /// <param name="width">Ancho visible; por omisión, el de <paramref name="luma"/>.</param>
    public GpuTextureSurface(GpuTexture luma, GpuTexture? chroma, FramePixelFormat format, VideoColor color, int? width = null, int? height = null,
        bool ownsTextures = true)
    {
        ArgumentNullException.ThrowIfNull(luma);
        Luma = luma;
        Chroma = chroma ?? luma;
        PixelFormat = format;
        Color = color;
        Width = width ?? luma.Width;
        Height = height ?? luma.Height;
        _ownsTextures = ownsTextures;
    }

    public GpuTexture Luma { get; }

    /// <summary>El plano de croma; en RGBA, la misma textura que <see cref="Luma"/>.</summary>
    public GpuTexture Chroma { get; }

    public FramePixelFormat PixelFormat { get; }

    public int Width { get; }

    public int Height { get; }

    public int CodedWidth => Luma.Width;

    public int CodedHeight => Luma.Height;

    public VideoColor Color { get; }

    public bool IsReleased => Volatile.Read(ref _references) == 0;

    public bool AddRef()
    {
        int current;
        do
        {
            current = Volatile.Read(ref _references);
            if (current == 0)
            {
                return false;
            }
        }
        while (Interlocked.CompareExchange(ref _references, current + 1, current) != current);

        return true;
    }

    public void Release()
    {
        if (Interlocked.Decrement(ref _references) == 0 && _ownsTextures)
        {
            Luma.Dispose();
            if (!ReferenceEquals(Chroma, Luma))
            {
                Chroma.Dispose();
            }
        }
    }

    public void Dispose() => Release();
}

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
