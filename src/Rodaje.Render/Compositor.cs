using System.Numerics;
using Rodaje.Media.Gpu;
using Rodaje.Media.Video;
using Rodaje.Playback.Composition;
using TerraFX.Interop.DirectX;
using static TerraFX.Interop.DirectX.D3D11;
using Rodaje.Render.Effects;
using Rodaje.Render.Shaders;
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
/// <param name="EffectsApplied">Efectos, mezclas distintas de la normal y transiciones aplicados.</param>
/// <param name="EffectsSkipped">
/// Los que no se aplicaron en este cuadro: shader todavía sin compilar, pool lleno, código desconocido o repetido.
/// </param>
/// <param name="MissingContent">LUT que todavía cargan o cuyo paquete falta, y transiciones de paquete sin resolver.</param>
/// <param name="PoolBytes">Memoria de los intermedios de efectos al terminar el cuadro.</param>
public readonly record struct CompositeResult(int LayersDrawn, int LayersSkipped, int EffectsApplied = 0, int EffectsSkipped = 0,
    int MissingContent = 0, long PoolBytes = 0);

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
    private readonly EffectPipeline _effects;
    private readonly RenderTargetPool _pool;
    private readonly LutTextures _luts;
    private readonly LayerRenderer _layers;
    private RenderTarget? _output;
    private ID3D11Texture2D* _staging;

    public Compositor(GpuDevice gpu, int width, int height, CompositeFormat format = CompositeFormat.Rgba16Float)
        : this(gpu, width, height, format, TimeProvider.System)
    {
    }

    /// <param name="shaders">Caché de los shaders de efectos; por omisión <see cref="ShaderCache.Default"/>.</param>
    /// <param name="poolCapBytes">Tope de los intermedios; por omisión según la VRAM (<see cref="PoolCapFor"/>).</param>
    public Compositor(GpuDevice gpu, int width, int height, CompositeFormat format, ShaderCache? shaders, long? poolCapBytes = null)
        : this(gpu, width, height, format, TimeProvider.System, shaders, poolCapBytes)
    {
    }

    internal Compositor(GpuDevice gpu, int width, int height, CompositeFormat format, TimeProvider time,
        ShaderCache? shaders = null, long? poolCapBytes = null)
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
        _pool = new RenderTargetPool(_device, poolCapBytes ?? PoolCapFor(gpu.DedicatedVideoMemory));
        _luts = new LutTextures(_device);
        _pipeline = new QuadPipeline(_device, _context);
        try
        {
            _effects = new EffectPipeline(_device, _context, shaders ?? ShaderCache.Default);
            _layers = new LayerRenderer(gpu, _pipeline, _effects, _views, _pool, _luts);
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

    /// <summary>
    /// <c>ID3D11Texture2D*</c> con el último cuadro compuesto, premultiplicado (alfa 1 en todo el cuadro salvo con
    /// <see cref="TransparentBackground"/>). Vale hasta <see cref="Dispose"/>.
    /// </summary>
    public nint Texture => _output is null ? 0 : (nint)_output.Texture;

    /// <summary><c>ID3D11ShaderResourceView*</c> de <see cref="Texture"/>, con todos sus mipmaps.</summary>
    public nint ShaderResourceView => _output is null ? 0 : (nint)_output.Srv;

    /// <summary>
    /// Compone sobre transparente en vez de sobre negro, para exportar con alfa: donde no hay capas el alfa es 0. Las
    /// mezclas por shader suponen un fondo opaco.
    /// </summary>
    public bool TransparentBackground { get; set; }

    /// <summary>Vistas de superficies del decodificador guardadas, para diagnóstico.</summary>
    public int CachedViewCount => _views.Count;

    /// <summary>De dónde salen las LUT. Sin proveedor, una capa con LUT sale sin ella y se cuenta en <see cref="CompositeResult.MissingContent"/>.</summary>
    public IEffectResources? Resources
    {
        get => _layers.Resources;
        set => _layers.Resources = value;
    }

    /// <summary>Termina cuando todos los shaders de efectos están listos. Mientras tanto los efectos se saltan y se cuentan.</summary>
    public Task ShadersReady => _effects.Ready;

    /// <summary>Tope de memoria de los intermedios de efectos.</summary>
    public long PoolCapBytes => _pool.CapBytes;

    /// <summary>Tope de los intermedios según la VRAM dedicada: 7,5 %, entre 96 y 300 MB.</summary>
    public static long PoolCapFor(long dedicatedVideoMemory) => RenderTargetPool.CapFor(dedicatedVideoMemory);

    /// <summary>Compone <paramref name="frame"/>.</summary>
    /// <param name="geometry">Relación de aspecto del píxel y rotación de cada capa; sin ella, píxeles cuadrados sin rotar.</param>
    /// <exception cref="ArgumentException">Una capa vive en otro dispositivo.</exception>
    public CompositeResult Render(CompositionFrame frame, Func<LayerFrame, SourceGeometry>? geometry = null)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ObjectDisposedException.ThrowIf(_output is null, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frame.Width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frame.Height);
        int drawn = 0;
        var stats = default(FrameStats);
        var destination = new Destination(_output, Width, Height, frame.Width, frame.Height);
        float pixelScale = destination.PixelScale;
        _gpu.LockContext();
        try
        {
            _pipeline.Bind(_output.Rtv, Width, Height, QuadPipeline.Shader.Layer);
            float* black = stackalloc float[] { 0f, 0f, 0f, TransparentBackground ? 0f : 1f };
            _context->ClearRenderTargetView(_output.Rtv, black);
            bool bound = true;
            IReadOnlyList<LayerFrame> layers = frame.Layers;
            for (int i = 0; i < layers.Count; i++)
            {
                LayerFrame layer = layers[i];
                if (_layers.Usable(layer) is not { } payload)
                {
                    stats.LayersSkipped++;
                    continue;
                }

                if (layer.IsPlain)
                {
                    if (!bound)
                    {
                        _pipeline.Bind(_output.Rtv, Width, Height, QuadPipeline.Shader.Layer);
                        bound = true;
                    }

                    if (_layers.DrawPlain(layer, payload, geometry?.Invoke(layer) ?? SourceGeometry.Square, frame.Width, frame.Height, pixelScale))
                    {
                        drawn++;
                    }

                    continue;
                }

                bound = false;
                if (_layers.Draw(layer, payload, geometry, destination, ref stats))
                {
                    drawn++;
                }
            }
        }
        finally
        {
            _pipeline.Unbind();
            _views.EndFrame();
            _pool.EndFrame();
            _gpu.UnlockContext();
        }

        return new CompositeResult(drawn, stats.LayersSkipped, stats.EffectsApplied, stats.EffectsSkipped, stats.MissingContent, _pool.Bytes);
    }

    /// <summary>Copia el último cuadro compuesto a la CPU en RGBA de 8 bits, fila tras fila (para pruebas y miniaturas).</summary>
    public byte[] ReadbackRgba()
    {
        ObjectDisposedException.ThrowIf(_output is null, this);
        var pixels = new byte[Width * Height * 4];
        _gpu.LockContext();
        try
        {
            ID3D11Texture2D* staging = Staging();
            _context->CopySubresourceRegion((ID3D11Resource*)staging, 0, 0, 0, 0, (ID3D11Resource*)_output.Texture, 0, null);
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
            _pool.Trim();
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
        _pool.Dispose();
        _luts.Dispose();
        _effects?.Dispose();
        _pipeline?.Dispose();
        Com.Release(ref _staging);
        _output?.Dispose();
        _output = null;
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
        ID3D11RenderTargetView* rtv = null;
        ID3D11ShaderResourceView* srv = null;
        try
        {
            var rtvDesc = new D3D11_RENDER_TARGET_VIEW_DESC { Format = format, ViewDimension = D3D11_RTV_DIMENSION.D3D11_RTV_DIMENSION_TEXTURE2D };
            rtvDesc.Texture2D.MipSlice = 0;
            Check(_device->CreateRenderTargetView((ID3D11Resource*)target, &rtvDesc, &rtv), "CreateRenderTargetView(composición)");
            Check(_device->CreateShaderResourceView((ID3D11Resource*)target, null, &srv), "CreateShaderResourceView(composición)");
        }
        catch
        {
            Com.Release(ref rtv);
            Com.Release(ref target);
            throw;
        }

        _output = new RenderTarget(target, rtv, srv, Width, Height, format, RenderTarget.BytesFor(Width, Height, format));
    }

    private ID3D11Texture2D* Staging()
    {
        if (_staging != null)
        {
            return _staging;
        }

        D3D11_TEXTURE2D_DESC desc;
        _output!.Texture->GetDesc(&desc);
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
