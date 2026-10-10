using System.Numerics;
using Rodaje.Media.Gpu;
using Rodaje.Media.Video;
using Rodaje.Playback.Composition;
using TerraFX.Interop.DirectX;
using static TerraFX.Interop.DirectX.D3D11;
using static Rodaje.Render.RenderException;

namespace Rodaje.Render;

/// <summary>Formato del destino de la composición.</summary>
public enum CompositeFormat
{
    /// <summary>
    /// <c>R16G16B16A16_FLOAT</c>: conserva los 10 bits de P010 y no cuantiza a 8 bits en cada mezcla; deja margen
    /// para mezclar en luz lineal y para efectos. Ocupa el doble de memoria y de ancho de banda.
    /// </summary>
    Rgba16Float,

    /// <summary><c>R8G8B8A8_UNORM</c>: la mitad de memoria; cada capa translúcida redondea a 8 bits.</summary>
    Rgba8Unorm,
}

/// <summary>Resultado de <see cref="Compositor.Render"/>.</summary>
/// <param name="LayersDrawn">Capas dibujadas.</param>
/// <param name="LayersSkipped">Capas sin textura D3D11 utilizable (contenido de CPU, liberado o de otro formato).</param>
public readonly record struct CompositeResult(int LayersDrawn, int LayersSkipped);

/// <summary>
/// Compone un <see cref="CompositionFrame"/> en una textura propia: fondo negro y cada capa de abajo arriba,
/// convertida de Y'CbCr a R'G'B', ajustada al cuadro, transformada y mezclada con "over" premultiplicado.
/// </summary>
/// <remarks>
/// <para>
/// La mezcla se hace sobre valores con la curva del video (no en luz lineal), igual que la mayoría de editores en
/// modo de 8 bits: una opacidad de 0,5 da el promedio de los códigos. El muestreo es bilineal sobre el plano del
/// decodificador, que no tiene mipmaps: al reducir una capa a menos de la mitad aparece aliasing. El presentador sí
/// usa mipmaps de la composición.
/// </para>
/// <para>
/// El tamaño del compositor puede ser menor que el de la secuencia (vista previa a media resolución): las posiciones
/// de <see cref="LayerTransform"/> siguen en píxeles de la secuencia y se escalan al destino.
/// </para>
/// </remarks>
public sealed unsafe class Compositor : IDisposable
{
    private readonly GpuDevice _gpu;
    private readonly ID3D11Device* _device;
    private readonly ID3D11DeviceContext* _context;
    private readonly QuadPipeline _pipeline;
    private readonly PlaneViewCache _views;
    private ID3D11Texture2D* _target;
    private ID3D11RenderTargetView* _targetView;
    private ID3D11ShaderResourceView* _shaderView;
    private ID3D11Texture2D* _staging;

    public Compositor(GpuDevice gpu, int width, int height, CompositeFormat format = CompositeFormat.Rgba16Float)
        : this(gpu, width, height, format, TimeProvider.System)
    {
    }

    internal Compositor(GpuDevice gpu, int width, int height, CompositeFormat format, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(gpu);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(width, D3D11_REQ_TEXTURE2D_U_OR_V_DIMENSION);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(height, D3D11_REQ_TEXTURE2D_U_OR_V_DIMENSION);
        _gpu = gpu;
        _device = (ID3D11Device*)gpu.NativeDevice;
        _context = (ID3D11DeviceContext*)gpu.NativeContext;
        Width = width;
        Height = height;
        Format = format;
        MipLevels = 1 + (int)Math.Log2(Math.Max(width, height));
        _views = new PlaneViewCache(_device, time);
        _pipeline = new QuadPipeline(_device, _context);
        try
        {
            CreateTarget();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public GpuDevice Device => _gpu;

    public int Width { get; }

    public int Height { get; }

    public CompositeFormat Format { get; }

    /// <summary>Niveles de mipmap de <see cref="Texture"/>; solo el 0 se dibuja y el presentador genera el resto.</summary>
    public int MipLevels { get; }

    /// <summary><c>ID3D11Texture2D*</c> con el último cuadro compuesto (alfa 1 en todo el cuadro). Vale hasta <see cref="Dispose"/>.</summary>
    public nint Texture => (nint)_target;

    /// <summary><c>ID3D11ShaderResourceView*</c> de <see cref="Texture"/>, con todos sus mipmaps.</summary>
    public nint ShaderResourceView => (nint)_shaderView;

    /// <summary>Vistas de superficies del decodificador guardadas, para diagnóstico.</summary>
    public int CachedViewCount => _views.Count;

    /// <summary>Compone <paramref name="frame"/>.</summary>
    /// <param name="geometry">Relación de aspecto del píxel y rotación de cada capa; sin ella, píxeles cuadrados sin rotar.</param>
    /// <exception cref="ArgumentException">Una capa vive en otro dispositivo.</exception>
    public CompositeResult Render(CompositionFrame frame, Func<LayerFrame, SourceGeometry>? geometry = null)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ObjectDisposedException.ThrowIf(_target == null, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frame.Width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frame.Height);
        int drawn = 0;
        int skipped = 0;
        _gpu.LockContext();
        try
        {
            _pipeline.Bind(_targetView, Width, Height, QuadPipeline.Shader.Layer);
            float* black = stackalloc float[] { 0f, 0f, 0f, 1f };
            _context->ClearRenderTargetView(_targetView, black);
            foreach (LayerFrame layer in frame.Layers)
            {
                if (layer.Frame.Payload is not D3D11TexturePayload payload || payload.IsReleased
                    || payload.PixelFormat is not (FramePixelFormat.Nv12 or FramePixelFormat.P010))
                {
                    skipped++;
                    continue;
                }

                if (payload.Device != _gpu)
                {
                    throw new ArgumentException($"La capa de la pista {layer.TrackIndex} ({layer.ClipId}) es una textura de otro dispositivo.", nameof(frame));
                }

                if (DrawLayer(layer, payload, geometry?.Invoke(layer) ?? SourceGeometry.Square, frame.Width, frame.Height))
                {
                    drawn++;
                }
            }
        }
        finally
        {
            _pipeline.Unbind();
            _views.EndFrame();
            _gpu.UnlockContext();
        }

        return new CompositeResult(drawn, skipped);
    }

    /// <summary>Copia el último cuadro compuesto a la CPU en RGBA de 8 bits, fila tras fila (para pruebas y miniaturas).</summary>
    public byte[] ReadbackRgba()
    {
        ObjectDisposedException.ThrowIf(_target == null, this);
        var pixels = new byte[Width * Height * 4];
        _gpu.LockContext();
        try
        {
            ID3D11Texture2D* staging = Staging();
            _context->CopySubresourceRegion((ID3D11Resource*)staging, 0, 0, 0, 0, (ID3D11Resource*)_target, 0, null);
            D3D11_MAPPED_SUBRESOURCE mapped;
            Check(_context->Map((ID3D11Resource*)staging, 0, D3D11_MAP.D3D11_MAP_READ, 0, &mapped), "Map(lectura)");
            try
            {
                for (int y = 0; y < Height; y++)
                {
                    byte* row = (byte*)mapped.pData + ((nuint)y * mapped.RowPitch);
                    Span<byte> target = pixels.AsSpan(y * Width * 4, Width * 4);
                    if (Format == CompositeFormat.Rgba8Unorm)
                    {
                        new ReadOnlySpan<byte>(row, Width * 4).CopyTo(target);
                        continue;
                    }

                    var halves = new ReadOnlySpan<Half>(row, Width * 4);
                    for (int i = 0; i < target.Length; i++)
                    {
                        target[i] = (byte)Math.Round(Math.Clamp((float)halves[i], 0f, 1f) * 255f);
                    }
                }
            }
            finally
            {
                _context->Unmap((ID3D11Resource*)staging, 0);
            }
        }
        finally
        {
            _gpu.UnlockContext();
        }

        return pixels;
    }

    /// <summary>
    /// Suelta ya las vistas de decodificadores cerrados y las que llevan tiempo sin usarse. <see cref="Render"/> lo hace
    /// solo; en pausa, cuando no se compone, quien dibuja lo llama de vez en cuando para no retener esos pools.
    /// </summary>
    public void TrimCachedViews()
    {
        _gpu.LockContext();
        try
        {
            _views.Trim();
        }
        finally
        {
            _gpu.UnlockContext();
        }
    }

    /// <summary>Suelta las vistas guardadas de superficies del decodificador (por ejemplo, al cerrar una fuente).</summary>
    public void ReleaseCachedViews()
    {
        _gpu.LockContext();
        try
        {
            _views.Clear();
        }
        finally
        {
            _gpu.UnlockContext();
        }
    }

    public void Dispose()
    {
        _views.Dispose();
        _pipeline?.Dispose();
        Com.Release(ref _shaderView);
        Com.Release(ref _targetView);
        Com.Release(ref _staging);
        Com.Release(ref _target);
    }

    private static QuadConstants LayerConstants(D3D11TexturePayload payload, in LayerPlacement placement, SourceGeometry geometry,
        float opacity, float sequenceWidth, float sequenceHeight, float pixelScale)
    {
        bool tenBit = payload.PixelFormat == FramePixelFormat.P010;
        YuvRange range = YuvRange.For(tenBit, payload.Color.FullRange);
        YuvToRgb toRgb = YuvToRgb.For(payload.Color.Matrix);
        float uMax = (float)payload.Width / payload.CodedWidth;
        float vMax = (float)payload.Height / payload.CodedHeight;
        QuadTexCoords uv = VideoLayout.Corners(geometry.Rotation, uMax, vMax);
        float screenScale = placement.Scale * pixelScale;
        // Un píxel de destino más allá del borde, para que la cobertura lo suavice también hacia afuera.
        float margin = 1f / screenScale;
        float w = placement.LocalWidth;
        float h = placement.LocalHeight;
        var constants = new QuadConstants
        {
            Range = new(range.LumaScale, range.LumaBias, range.ChromaScale, range.ChromaBias),
            ToRgb = new(toRgb.RedFromCr, toRgb.GreenFromCb, toRgb.GreenFromCr, toRgb.BlueFromCb),
            Limits = new(
                (payload.Width - 0.5f) / payload.CodedWidth,
                (payload.Height - 0.5f) / payload.CodedHeight,
                (payload.Width - 1f) / payload.CodedWidth,
                (payload.Height - 1f) / payload.CodedHeight),
            Shape = new(opacity, screenScale, w, h),
        };
        constants.TopLeft = Corner(placement, uv, -margin, -margin, sequenceWidth, sequenceHeight, out constants.LocalTopLeft);
        constants.TopRight = Corner(placement, uv, w + margin, -margin, sequenceWidth, sequenceHeight, out constants.LocalTopRight);
        constants.BottomLeft = Corner(placement, uv, -margin, h + margin, sequenceWidth, sequenceHeight, out constants.LocalBottomLeft);
        constants.BottomRight = Corner(placement, uv, w + margin, h + margin, sequenceWidth, sequenceHeight, out constants.LocalBottomRight);
        return constants;
    }

    private static Float4 Corner(in LayerPlacement placement, in QuadTexCoords uv, float x, float y, float sequenceWidth, float sequenceHeight,
        out Float4 local)
    {
        Vector2 ndc = LayerLayout.ToNdc(placement.Map(x, y), sequenceWidth, sequenceHeight);
        TexCoord t = LayerLayout.TexCoordAt(uv, x, y, placement.LocalWidth, placement.LocalHeight);
        local = new Float4(x, y, 0, 0);
        return new Float4(ndc.X, ndc.Y, t.U, t.V);
    }

    private bool DrawLayer(LayerFrame layer, D3D11TexturePayload payload, SourceGeometry geometry, int sequenceWidth, int sequenceHeight)
    {
        double opacity = layer.Transform.Opacity;
        if (!(opacity > 0))
        {
            return false;
        }

        (int displayWidth, int displayHeight) = layer.DisplayWidth > 0 && layer.DisplayHeight > 0
            ? (layer.DisplayWidth, layer.DisplayHeight)
            : geometry.DisplaySize(payload.Width, payload.Height);
        if (LayerLayout.Place(sequenceWidth, sequenceHeight, displayWidth, displayHeight, layer.Transform) is not { } placement)
        {
            return false;
        }

        float pixelScale = Math.Min((float)Width / sequenceWidth, (float)Height / sequenceHeight);
        QuadConstants constants = LayerConstants(payload, placement, geometry, (float)Math.Min(opacity, 1), sequenceWidth, sequenceHeight, pixelScale);
        PlaneViews views = _views.Get(payload, out bool owned);
        try
        {
            ID3D11ShaderResourceView** srvs = stackalloc ID3D11ShaderResourceView*[2] { views.Luma, views.Chroma };
            _context->PSSetShaderResources(0, 2, srvs);
            _pipeline.Draw(constants);
            if (owned)
            {
                srvs[0] = null;
                srvs[1] = null;
                _context->PSSetShaderResources(0, 2, srvs);
            }
        }
        finally
        {
            if (owned)
            {
                views.Release();
            }
        }

        return true;
    }

    private void CreateTarget()
    {
        DXGI_FORMAT format = Format == CompositeFormat.Rgba8Unorm
            ? DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM
            : DXGI_FORMAT.DXGI_FORMAT_R16G16B16A16_FLOAT;
        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width = (uint)Width,
            Height = (uint)Height,
            MipLevels = (uint)MipLevels,
            ArraySize = 1,
            Format = format,
            SampleDesc = new DXGI_SAMPLE_DESC(1, 0),
            Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
            BindFlags = (uint)(D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET | D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE),
            MiscFlags = (uint)D3D11_RESOURCE_MISC_FLAG.D3D11_RESOURCE_MISC_GENERATE_MIPS,
        };
        ID3D11Texture2D* target;
        Check(_device->CreateTexture2D(&desc, null, &target), "CreateTexture2D(composición)");
        _target = target;

        var rtvDesc = new D3D11_RENDER_TARGET_VIEW_DESC { Format = format, ViewDimension = D3D11_RTV_DIMENSION.D3D11_RTV_DIMENSION_TEXTURE2D };
        rtvDesc.Texture2D.MipSlice = 0;
        ID3D11RenderTargetView* rtv;
        Check(_device->CreateRenderTargetView((ID3D11Resource*)target, &rtvDesc, &rtv), "CreateRenderTargetView(composición)");
        _targetView = rtv;

        ID3D11ShaderResourceView* srv;
        Check(_device->CreateShaderResourceView((ID3D11Resource*)target, null, &srv), "CreateShaderResourceView(composición)");
        _shaderView = srv;
    }

    private ID3D11Texture2D* Staging()
    {
        if (_staging != null)
        {
            return _staging;
        }

        D3D11_TEXTURE2D_DESC desc;
        _target->GetDesc(&desc);
        desc.MipLevels = 1;
        desc.Usage = D3D11_USAGE.D3D11_USAGE_STAGING;
        desc.BindFlags = 0;
        desc.MiscFlags = 0;
        desc.CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ;
        ID3D11Texture2D* staging;
        Check(_device->CreateTexture2D(&desc, null, &staging), "CreateTexture2D(lectura)");
        _staging = staging;
        return staging;
    }
}
