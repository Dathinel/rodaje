using Rodaje.Media.Video;
using TerraFX.Interop.DirectX;
using FF = FFmpeg.AutoGen.Abstractions;

namespace Rodaje.Media.Gpu;

/// <summary>Contenido de un cuadro que vive en una textura D3D11 de <see cref="Device"/>.</summary>
/// <remarks>
/// <para>
/// Es el único tipo que expone un puntero nativo: <see cref="Texture"/> es un <c>ID3D11Texture2D*</c> válido
/// mientras quede una referencia. Nace con una, la de su dueño (normalmente el <c>DecodedFrame</c>), que
/// se devuelve con <see cref="Dispose"/>. Quien necesite la textura más allá (por ejemplo, el cuadro que
/// sigue en pantalla) toma otra con <see cref="AddRef"/> y la devuelve con <see cref="Release"/>.
/// </para>
/// <para>
/// Un cuadro de D3D11VA es una capa (<see cref="ArraySlice"/>) de un arreglo de texturas compartido por el
/// pool del decodificador: retenerlo impide que el decodificador reutilice esa capa. La textura puede
/// medir más que la imagen (<see cref="CodedWidth"/> frente a <see cref="Width"/>): se recorta al muestrear.
/// </para>
/// </remarks>
public sealed unsafe class D3D11TexturePayload : IDisposable
{
    private static long s_live;

    private int _references = 1;
    private int _ownerReleased;
    private FF.AVFrame* _frame;
    private ID3D11Texture2D* _ownedTexture;

    private D3D11TexturePayload(GpuDevice device, ID3D11Texture2D* texture, int arraySlice, int width, int height)
    {
        D3D11_TEXTURE2D_DESC desc;
        texture->GetDesc(&desc);
        Device = device;
        Texture = (nint)texture;
        ArraySlice = arraySlice;
        DxgiFormat = (int)desc.Format;
        PixelFormat = desc.Format switch
        {
            DXGI_FORMAT.DXGI_FORMAT_NV12 => FramePixelFormat.Nv12,
            DXGI_FORMAT.DXGI_FORMAT_P010 => FramePixelFormat.P010,
            _ => FramePixelFormat.Other,
        };
        CodedWidth = (int)desc.Width;
        CodedHeight = (int)desc.Height;
        Width = width;
        Height = height;
        Interlocked.Increment(ref s_live);
    }

    public GpuDevice Device { get; }

    /// <summary><c>ID3D11Texture2D*</c>; puede ser un arreglo de texturas.</summary>
    public nint Texture { get; }

    public int ArraySlice { get; }

    /// <summary>Valor de <c>DXGI_FORMAT</c>: 103 (NV12) o 104 (P010).</summary>
    public int DxgiFormat { get; }

    public FramePixelFormat PixelFormat { get; }

    /// <summary>Tamaño de la textura, con el relleno que pide el decodificador.</summary>
    public int CodedWidth { get; }

    public int CodedHeight { get; }

    /// <summary>Tamaño visible de la imagen.</summary>
    public int Width { get; }

    public int Height { get; }

    /// <summary>Matriz y rango con que el renderizador debe convertir las muestras a RGB.</summary>
    public VideoColor Color { get; private init; } = VideoColor.Bt709Limited;

    /// <summary>La textura es del pool de D3D11VA (y no una subida desde la CPU).</summary>
    public bool IsDecoderSurface => _frame != null;

    public bool IsReleased => Volatile.Read(ref _references) == 0;

    /// <summary>Contenidos vivos en todo el proceso; las pruebas lo usan para detectar fugas.</summary>
    internal static long LiveCount => Interlocked.Read(ref s_live);

    /// <summary>Toma una referencia más; devuelve <c>false</c> si la textura ya se liberó.</summary>
    public bool AddRef()
    {
        int current = Volatile.Read(ref _references);
        while (current > 0)
        {
            int seen = Interlocked.CompareExchange(ref _references, current + 1, current);
            if (seen == current)
            {
                return true;
            }

            current = seen;
        }

        return false;
    }

    public void Release()
    {
        int remaining = Interlocked.Decrement(ref _references);
        if (remaining == 0)
        {
            Free();
        }
        else if (remaining < 0)
        {
            throw new InvalidOperationException("Se liberó una textura de cuadro más veces de las que se retuvo.");
        }
    }

    /// <summary>Devuelve la referencia del dueño; llamarlo de nuevo no hace nada.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _ownerReleased, 1) == 0)
        {
            Release();
        }
    }

    /// <summary>Envuelve un cuadro de D3D11VA y se queda con <paramref name="frame"/>.</summary>
    internal static D3D11TexturePayload FromDecoderFrame(GpuDevice device, FF.AVFrame* frame, VideoColor color)
    {
        var texture = (ID3D11Texture2D*)frame->data[0];
        int slice = (int)(nint)frame->data[1];
        return new D3D11TexturePayload(device, texture, slice, frame->width, frame->height) { _frame = frame, Color = color };
    }

    /// <summary>
    /// Envuelve una capa de una textura NV12 o P010 creada fuera del decodificador (generadores, proxies, pruebas).
    /// <paramref name="texture"/> es un <c>ID3D11Texture2D*</c> de <paramref name="device"/>, muestreable desde un shader;
    /// se toma una referencia propia y quien llama conserva la suya.
    /// </summary>
    public static D3D11TexturePayload FromTexture(GpuDevice device, nint texture, int arraySlice, int width, int height, VideoColor color)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentOutOfRangeException.ThrowIfZero(texture);
        ArgumentOutOfRangeException.ThrowIfNegative(arraySlice);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        var native = (ID3D11Texture2D*)texture;
        D3D11_TEXTURE2D_DESC desc;
        native->GetDesc(&desc);
        if (arraySlice >= desc.ArraySize || width > desc.Width || height > desc.Height)
        {
            throw new ArgumentOutOfRangeException(nameof(arraySlice),
                $"La capa {arraySlice} de {width}×{height} no cabe en una textura de {desc.Width}×{desc.Height} con {desc.ArraySize} capas.");
        }

        native->AddRef();
        return new D3D11TexturePayload(device, native, arraySlice, width, height) { _ownedTexture = native, Color = color };
    }

    /// <summary>Envuelve una textura propia y se queda con su referencia.</summary>
    internal static D3D11TexturePayload FromOwnedTexture(GpuDevice device, ID3D11Texture2D* texture, int width, int height, VideoColor color) =>
        new(device, texture, 0, width, height) { _ownedTexture = texture, Color = color };

    private void Free()
    {
        if (_frame != null)
        {
            FF.AVFrame* frame = _frame;
            _frame = null;
            FF.ffmpeg.av_frame_free(&frame);
        }

        if (_ownedTexture != null)
        {
            _ownedTexture->Release();
            _ownedTexture = null;
        }

        Interlocked.Decrement(ref s_live);
    }
}
