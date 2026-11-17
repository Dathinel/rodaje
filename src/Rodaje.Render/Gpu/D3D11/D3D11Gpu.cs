using System.Runtime.CompilerServices;
using Rodaje.Media.Gpu;
using Rodaje.Render.Shaders;
using TerraFX.Interop.DirectX;
using static TerraFX.Interop.DirectX.D3D11;
using static Rodaje.Render.RenderException;

namespace Rodaje.Render.Gpu.D3D11;

/// <summary>
/// <see cref="IGpu"/> sobre el <see cref="GpuDevice"/> de siempre: el contexto inmediato, compartido con el decodificador bajo su
/// candado. Un cuadro toma ese candado y lo suelta al cerrarse; cada orden va directa al contexto, así que el resultado es el de
/// llamar a D3D11 a mano.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows7.0")]
public sealed unsafe partial class D3D11Gpu : IGpu
{
    private static readonly ConditionalWeakTable<GpuDevice, D3D11Gpu> s_byDevice = [];
    private readonly bool _ownsDevice;
    private readonly ID3D11Device* _device;
    private readonly ID3D11DeviceContext* _context;
    private bool _disposed;

    // Recursos enlazados que hay que vaciar antes de otro enlace (se tocan con el candado tomado).
    internal int BoundPixelResources;
    internal int BoundComputeResources;
    internal int BoundStorage;
    internal int BoundTargets;
    internal int OpenFrames;

    // Lo último que un Draw dejó en el contexto, para no repetirlo (vale dentro de un cuadro: nadie más toca el contexto).
    internal D3D11Pipeline? DrawPipeline;
    internal readonly nint[] DrawTargets = new nint[GpuTargets.MaxColors + 1];
    internal int DrawTargetCount = -1;
    internal D3D11_VIEWPORT DrawViewport;
    internal readonly nint[] DrawConstants = new nint[GpuBindings.MaxConstants];
    internal int DrawConstantCount;
    internal readonly nint[] DrawSamplers = new nint[GpuBindings.MaxSamplers];
    internal int DrawSamplerCount;
    internal readonly nint[] DrawResources = new nint[GpuBindings.MaxResources];
    internal bool DrawResourcesKnown;

    internal void ForgetDrawState()
    {
        DrawPipeline = null;
        DrawTargetCount = -1;
        DrawConstantCount = 0;
        DrawSamplerCount = 0;
        DrawResourcesKnown = false;
    }

    private D3D11Gpu(GpuDevice device, bool ownsDevice)
    {
        Device = device;
        _ownsDevice = ownsDevice;
        _device = (ID3D11Device*)device.NativeDevice;
        _context = (ID3D11DeviceContext*)device.NativeContext;
        Caps = new GpuCaps
        {
            Adapter = device.AdapterName,
            IsSoftware = device.IsSoftware,
            MaxTextureSize = D3D11_REQ_TEXTURE2D_U_OR_V_DIMENSION,
            Raster = true,
            DepthReadback = false,
            MaxSamples = MaxSamplesFor,
            CanBlend = CanBlendTo,
        };
    }

    /// <summary>El dispositivo de siempre: lo comparten el decodificador, la presentación y lo que aún no pasó a <see cref="IGpu"/>.</summary>
    public GpuDevice Device { get; }

    public GpuCaps Caps { get; }

    public bool IsLost => Device.IsLost;

    /// <summary>El motor de <paramref name="device"/> (uno por dispositivo); no lo cierra.</summary>
    public static D3D11Gpu For(GpuDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return s_byDevice.GetValue(device, static d => new D3D11Gpu(d, ownsDevice: false));
    }

    /// <summary>Un dispositivo propio, que se cierra con <see cref="Dispose"/>.</summary>
    /// <param name="preferSoftware">WARP, el rasterizador de Direct3D en la CPU (para autopruebas y pruebas reproducibles).</param>
    public static D3D11Gpu Create(bool preferSoftware = false) =>
        new(preferSoftware ? GpuDevice.CreateWarp() : GpuDevice.Create(), ownsDevice: true);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_ownsDevice)
        {
            Device.Dispose();
        }
    }

    public IGpuFrame BeginFrame()
    {
        ObjectDisposedException.ThrowIf(_disposed || Device.IsDisposed, this);
        Device.LockContext();
        if (OpenFrames++ == 0)
        {
            // Entre cuadros el contexto lo usa también el código de fuera (decodificador, títulos): no se da nada por puesto.
            ForgetDrawState();
        }

        return new D3D11Frame(this);
    }

    public GpuTexture CreateTexture(in GpuTextureDesc desc) => D3D11Texture.Create(this, desc, default);

    public GpuTexture CreateTexture(in GpuTextureDesc desc, ReadOnlySpan<byte> initialData) => D3D11Texture.Create(this, desc, initialData);

    public GpuBuffer CreateBuffer(in GpuBufferDesc desc, ReadOnlySpan<byte> initialData = default) => D3D11Buffer.Create(this, desc, initialData);

    public GpuSampler CreateSampler(in GpuSamplerDesc desc)
    {
        var native = new D3D11_SAMPLER_DESC
        {
            Filter = desc.Filter == GpuFilter.Point ? D3D11_FILTER.D3D11_FILTER_MIN_MAG_MIP_POINT : D3D11_FILTER.D3D11_FILTER_MIN_MAG_MIP_LINEAR,
            AddressU = Address(desc.Address),
            AddressV = Address(desc.Address),
            AddressW = Address(desc.Address),
            MaxLOD = D3D11_FLOAT32_MAX,
        };
        ID3D11SamplerState* sampler;
        Check(_device->CreateSamplerState(&native, &sampler), "CreateSamplerState");
        return new D3D11Sampler(sampler);
    }

    public GpuShader CreateShader(ShaderVariant variant, ShaderCache? cache = null)
    {
        ArgumentNullException.ThrowIfNull(variant);
        byte[] code = (cache ?? ShaderCache.Default).GetOrCompile(variant);
        return CreateShader(code, variant.Profile, variant.ToString());
    }

    /// <summary>Con bytecode ya compilado para D3D11 (el perfil decide el tipo: <c>vs_</c>, <c>ps_</c> o <c>cs_</c>).</summary>
    public GpuShader CreateShader(byte[] code, string profile, string id)
    {
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(profile);
        void* shader;
        fixed (byte* bytes = code)
        {
            switch (profile[..2])
            {
                case "vs":
                    Check(_device->CreateVertexShader(bytes, (nuint)code.Length, null, (ID3D11VertexShader**)&shader), $"CreateVertexShader({id})");
                    break;
                case "ps":
                    Check(_device->CreatePixelShader(bytes, (nuint)code.Length, null, (ID3D11PixelShader**)&shader), $"CreatePixelShader({id})");
                    break;
                case "cs":
                    Check(_device->CreateComputeShader(bytes, (nuint)code.Length, null, (ID3D11ComputeShader**)&shader), $"CreateComputeShader({id})");
                    break;
                default:
                    throw new RenderException($"Perfil de sombreador desconocido: {profile}.");
            }
        }

        return new D3D11Shader((ID3D11DeviceChild*)shader, profile[..2], profile[..2] == "vs" ? code : null) { Id = id };
    }

    public GpuPipeline CreatePipeline(GpuPipelineDesc desc) => D3D11Pipeline.Create(this, desc);

    public GpuTimerQuery CreateTimer() => new D3D11TimerQuery(this);

    public GpuReadback CreateReadback(int width, int height, GpuFormat format) => D3D11Readback.ForTexture(this, width, height, format);

    public GpuReadback CreateReadback(int bytes) => D3D11Readback.ForBuffer(this, bytes);

    public GpuSurfaceCache CreateSurfaceCache(TimeProvider? time = null) => new D3D11SurfaceCache(this, time);

    /// <summary>Una cadena de intercambio para composición (<c>SwapChainPanel</c>); <see cref="D3D11Swapchain.Native"/> la da.</summary>
    public IGpuSwapchain CreateSwapchain(int width, int height, GpuFormat format)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        return new D3D11Swapchain(this, width, height, format);
    }

    /// <summary>Quita los destinos del contexto (con el candado tomado), para que la cadena pueda soltar sus búferes.</summary>
    internal void NativeContextUnbindTargets()
    {
        _context->OMSetRenderTargets(0, null, null);
        BoundTargets = 0;
        ForgetDrawState();
    }

    internal ID3D11Device* NativeDevice => _device;

    internal ID3D11DeviceContext* Context => _context;

    /// <summary>
    /// Una textura que no creó este motor (el búfer trasero de la cadena, una superficie de otro), con una referencia propia;
    /// las vistas que se pasan quedan a cargo del motor. Para el paso a <see cref="IGpu"/> por partes.
    /// </summary>
    internal GpuTexture Wrap(ID3D11Texture2D* texture, in GpuTextureDesc desc, ID3D11ShaderResourceView* srv = null,
        ID3D11RenderTargetView* rtv = null) => D3D11Texture.Wrap(this, texture, desc, srv, rtv);

    internal static D3D11_TEXTURE_ADDRESS_MODE Address(GpuAddressMode mode) => mode switch
    {
        GpuAddressMode.Wrap => D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_WRAP,
        GpuAddressMode.Mirror => D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_MIRROR,
        GpuAddressMode.Border => D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_BORDER,
        _ => D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP,
    };

    internal static DXGI_FORMAT Native(GpuFormat format) => format switch
    {
        GpuFormat.R8Unorm => DXGI_FORMAT.DXGI_FORMAT_R8_UNORM,
        GpuFormat.Rg8Unorm => DXGI_FORMAT.DXGI_FORMAT_R8G8_UNORM,
        GpuFormat.R16Unorm => DXGI_FORMAT.DXGI_FORMAT_R16_UNORM,
        GpuFormat.Rg16Unorm => DXGI_FORMAT.DXGI_FORMAT_R16G16_UNORM,
        GpuFormat.R16Uint => DXGI_FORMAT.DXGI_FORMAT_R16_UINT,
        GpuFormat.R32Uint => DXGI_FORMAT.DXGI_FORMAT_R32_UINT,
        GpuFormat.R32Float => DXGI_FORMAT.DXGI_FORMAT_R32_FLOAT,
        GpuFormat.Rg32Float => DXGI_FORMAT.DXGI_FORMAT_R32G32_FLOAT,
        GpuFormat.Rgba8Unorm => DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM,
        GpuFormat.Bgra8Unorm => DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
        GpuFormat.Rgb10A2Unorm => DXGI_FORMAT.DXGI_FORMAT_R10G10B10A2_UNORM,
        GpuFormat.Rgba16Unorm => DXGI_FORMAT.DXGI_FORMAT_R16G16B16A16_UNORM,
        GpuFormat.Rgba16Float => DXGI_FORMAT.DXGI_FORMAT_R16G16B16A16_FLOAT,
        GpuFormat.Rgba32Float => DXGI_FORMAT.DXGI_FORMAT_R32G32B32A32_FLOAT,
        GpuFormat.D32Float => DXGI_FORMAT.DXGI_FORMAT_D32_FLOAT,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Formato sin equivalente en D3D11."),
    };

    internal static GpuFormat FromNative(DXGI_FORMAT format)
    {
        for (GpuFormat f = GpuFormat.R8Unorm; f <= GpuFormat.D32Float; f++)
        {
            if (Native(f) == format)
            {
                return f;
            }
        }

        return GpuFormat.Unknown;
    }

    private int MaxSamplesFor(GpuFormat format)
    {
        int max = 1;
        for (uint count = 2; count <= 8; count *= 2)
        {
            uint levels;
            if (_device->CheckMultisampleQualityLevels(Native(format), count, &levels).SUCCEEDED && levels > 0)
            {
                max = (int)count;
            }
        }

        return max;
    }

    private bool CanBlendTo(GpuFormat format)
    {
        uint support;
        return _device->CheckFormatSupport(Native(format), &support).SUCCEEDED
            && (support & (uint)D3D11_FORMAT_SUPPORT.D3D11_FORMAT_SUPPORT_BLENDABLE) != 0;
    }
}
