using System.Runtime.InteropServices;
using Rodaje.Engine.Presentation;
using Rodaje.Media.Gpu;
using Rodaje.Media.Video;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.DirectX.D3D11;
using static TerraFX.Interop.DirectX.DirectX;
using static TerraFX.Interop.DirectX.DXGI;
using static TerraFX.Interop.Windows.Windows;

namespace Rodaje.Visor;

/// <summary>Qué pintar en un refresco.</summary>
/// <param name="DisplayWidth">Ancho en pantalla con SAR y rotación aplicados (MediaProbe).</param>
/// <param name="Progress">Posición entre 0 y 1, para la barra.</param>
internal readonly record struct RenderRequest(
    D3D11TexturePayload? Frame,
    int DisplayWidth,
    int DisplayHeight,
    int Rotation,
    double Progress,
    bool ShowBar,
    bool BarHighlighted);

/// <summary>Cadena de intercambio flip-model con latencia de un cuadro y el paso de NV12/P010 a RGB.</summary>
internal sealed unsafe class VideoRenderer : IDisposable
{
    public const int BarHeight = 5;
    public const int BarHeightHighlighted = 10;

    private const uint SwapChainFlags = (uint)DXGI_SWAP_CHAIN_FLAG.DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT;
    private const int MaxCachedViews = 96;

    private readonly GpuDevice _gpu;
    private readonly ID3D11Device* _device;
    private readonly ID3D11DeviceContext* _context;
    private readonly Dictionary<(nint Texture, int Slice), PlaneViews> _views = [];
    private IDXGISwapChain2* _swapChain;
    private ID3D11RenderTargetView* _target;
    private ID3D11VertexShader* _vertexShader;
    private ID3D11PixelShader* _videoShader;
    private ID3D11PixelShader* _solidShader;
    private ID3D11Buffer* _constants;
    private ID3D11SamplerState* _sampler;
    private HANDLE _frameWait;

    public VideoRenderer(GpuDevice gpu, HWND window)
    {
        _gpu = gpu;
        _device = (ID3D11Device*)gpu.NativeDevice;
        _context = (ID3D11DeviceContext*)gpu.NativeContext;
        try
        {
            CreateSwapChain(window);
            CreatePipeline();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>Se señala cuando la cadena admite otro cuadro; esperarla antes de pintar da la latencia mínima.</summary>
    public HANDLE FrameWaitHandle => _frameWait;

    public int Width { get; private set; }

    public int Height { get; private set; }

    public void Resize(int width, int height)
    {
        if (width <= 0 || height <= 0 || (width == Width && height == Height))
        {
            return;
        }

        _gpu.LockContext();
        try
        {
            _context->OMSetRenderTargets(0, null, null);
            ReleaseTarget();
            _context->Flush();
            Check(_swapChain->ResizeBuffers(0, (uint)width, (uint)height, DXGI_FORMAT.DXGI_FORMAT_UNKNOWN, SwapChainFlags), "ResizeBuffers");
            CreateTarget();
        }
        finally
        {
            _gpu.UnlockContext();
        }

        Width = width;
        Height = height;
    }

    /// <summary>Suelta las vistas de las texturas del decodificador (al cambiar de archivo).</summary>
    public void ReleaseFrameViews()
    {
        foreach (PlaneViews views in _views.Values)
        {
            views.Release();
        }

        _views.Clear();
    }

    /// <summary>Pinta y presenta con vsync. Devuelve <c>false</c> si la ventana está tapada (no se mostró nada).</summary>
    public bool Render(in RenderRequest request)
    {
        _gpu.LockContext();
        try
        {
            ID3D11RenderTargetView* target = _target;
            _context->OMSetRenderTargets(1, &target, null);
            float* black = stackalloc float[] { 0f, 0f, 0f, 1f };
            _context->ClearRenderTargetView(_target, black);
            var viewport = new D3D11_VIEWPORT { Width = Width, Height = Height, MaxDepth = 1 };
            _context->RSSetViewports(1, &viewport);
            _context->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY.D3D_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP);
            _context->IASetInputLayout(null);
            _context->VSSetShader(_vertexShader, null, 0);
            ID3D11Buffer* constants = _constants;
            _context->VSSetConstantBuffers(0, 1, &constants);
            _context->PSSetConstantBuffers(0, 1, &constants);
            ID3D11SamplerState* sampler = _sampler;
            _context->PSSetSamplers(0, 1, &sampler);

            if (request.Frame is { IsReleased: false } frame)
            {
                DrawVideo(frame, request);
            }

            if (request.ShowBar)
            {
                DrawBar(request.Progress, request.BarHighlighted);
            }
        }
        finally
        {
            _gpu.UnlockContext();
        }

        HRESULT hr = _swapChain->Present(1, 0);
        if (hr == DXGI_STATUS_OCCLUDED)
        {
            return false;
        }

        Check(hr, "Present");
        return true;
    }

    public void Dispose()
    {
        ReleaseFrameViews();
        ReleaseTarget();
        Release(ref _constants);
        Release(ref _sampler);
        Release(ref _vertexShader);
        Release(ref _videoShader);
        Release(ref _solidShader);
        Release(ref _swapChain);
        _frameWait = HANDLE.NULL;
    }

    private static void Release<T>(ref T* pointer)
        where T : unmanaged, IUnknown.Interface
    {
        if (pointer != null)
        {
            pointer->Release();
            pointer = null;
        }
    }

    private static void Check(HRESULT hr, string operation)
    {
        if (hr.FAILED)
        {
            string hint = hr == DXGI_ERROR_DEVICE_REMOVED || hr == DXGI_ERROR_DEVICE_RESET
                ? " La GPU se reinició o se perdió (por ejemplo, al actualizar el controlador); vuelve a abrir el visor."
                : "";
            throw new InvalidOperationException($"{operation} falló con HRESULT 0x{hr.Value:X8}.{hint}");
        }
    }

    private static (DXGI_FORMAT Luma, DXGI_FORMAT Chroma) PlaneFormats(FramePixelFormat format) => format switch
    {
        FramePixelFormat.P010 => (DXGI_FORMAT.DXGI_FORMAT_R16_UNORM, DXGI_FORMAT.DXGI_FORMAT_R16G16_UNORM),
        _ => (DXGI_FORMAT.DXGI_FORMAT_R8_UNORM, DXGI_FORMAT.DXGI_FORMAT_R8G8_UNORM),
    };

    private void CreateSwapChain(HWND window)
    {
        IDXGIDevice* dxgiDevice = null;
        IDXGIAdapter* adapter = null;
        IDXGIFactory2* factory = null;
        IDXGISwapChain1* swapChain1 = null;
        try
        {
            Check(_device->QueryInterface(__uuidof<IDXGIDevice>(), (void**)&dxgiDevice), "QueryInterface(IDXGIDevice)");
            Check(dxgiDevice->GetAdapter(&adapter), "GetAdapter");
            Check(adapter->GetParent(__uuidof<IDXGIFactory2>(), (void**)&factory), "GetParent(IDXGIFactory2)");
            var desc = new DXGI_SWAP_CHAIN_DESC1
            {
                Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
                SampleDesc = new DXGI_SAMPLE_DESC(1, 0),
                BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT,
                BufferCount = 2,
                Scaling = DXGI_SCALING.DXGI_SCALING_STRETCH,
                SwapEffect = DXGI_SWAP_EFFECT.DXGI_SWAP_EFFECT_FLIP_DISCARD,
                AlphaMode = DXGI_ALPHA_MODE.DXGI_ALPHA_MODE_IGNORE,
                Flags = SwapChainFlags,
            };
            Check(factory->CreateSwapChainForHwnd((IUnknown*)_device, window, &desc, null, null, &swapChain1), "CreateSwapChainForHwnd");
            // El visor hace su propia pantalla completa sin bordes; Alt+Enter de DXGI pediría el modo exclusivo.
            factory->MakeWindowAssociation(window, DXGI_MWA_NO_ALT_ENTER);
            IDXGISwapChain2* swapChain;
            Check(swapChain1->QueryInterface(__uuidof<IDXGISwapChain2>(), (void**)&swapChain), "QueryInterface(IDXGISwapChain2)");
            _swapChain = swapChain;
            Check(_swapChain->SetMaximumFrameLatency(1), "SetMaximumFrameLatency");
            _frameWait = _swapChain->GetFrameLatencyWaitableObject();
            DXGI_SWAP_CHAIN_DESC1 actual;
            Check(_swapChain->GetDesc1(&actual), "GetDesc1");
            Width = (int)actual.Width;
            Height = (int)actual.Height;
            CreateTarget();
        }
        finally
        {
            Release(ref swapChain1);
            Release(ref factory);
            Release(ref adapter);
            Release(ref dxgiDevice);
        }
    }

    private void CreateTarget()
    {
        ID3D11Texture2D* buffer;
        Check(_swapChain->GetBuffer(0, __uuidof<ID3D11Texture2D>(), (void**)&buffer), "GetBuffer");
        try
        {
            ID3D11RenderTargetView* target;
            Check(_device->CreateRenderTargetView((ID3D11Resource*)buffer, null, &target), "CreateRenderTargetView");
            _target = target;
        }
        finally
        {
            buffer->Release();
        }
    }

    private void ReleaseTarget() => Release(ref _target);

    private void CreatePipeline()
    {
        byte[] vs = Shaders.VertexShader();
        byte[] video = Shaders.VideoPixelShader();
        byte[] solid = Shaders.SolidPixelShader();
        fixed (byte* v = vs, p = video, s = solid)
        {
            ID3D11VertexShader* vertex;
            Check(_device->CreateVertexShader(v, (nuint)vs.Length, null, &vertex), "CreateVertexShader");
            _vertexShader = vertex;
            ID3D11PixelShader* pixel;
            Check(_device->CreatePixelShader(p, (nuint)video.Length, null, &pixel), "CreatePixelShader(video)");
            _videoShader = pixel;
            Check(_device->CreatePixelShader(s, (nuint)solid.Length, null, &pixel), "CreatePixelShader(barra)");
            _solidShader = pixel;
        }

        var bufferDesc = new D3D11_BUFFER_DESC
        {
            ByteWidth = (uint)sizeof(FrameConstants),
            Usage = D3D11_USAGE.D3D11_USAGE_DYNAMIC,
            BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_CONSTANT_BUFFER,
            CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_WRITE,
        };
        ID3D11Buffer* constants;
        Check(_device->CreateBuffer(&bufferDesc, null, &constants), "CreateBuffer");
        _constants = constants;

        var samplerDesc = new D3D11_SAMPLER_DESC
        {
            Filter = D3D11_FILTER.D3D11_FILTER_MIN_MAG_MIP_LINEAR,
            AddressU = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP,
            AddressV = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP,
            AddressW = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP,
            MaxLOD = D3D11_FLOAT32_MAX,
        };
        ID3D11SamplerState* sampler;
        Check(_device->CreateSamplerState(&samplerDesc, &sampler), "CreateSamplerState");
        _sampler = sampler;
    }

    private void DrawVideo(D3D11TexturePayload frame, in RenderRequest request)
    {
        PixelRect rect = VideoLayout.Fit(Width, Height, request.DisplayWidth, request.DisplayHeight);
        if (rect.IsEmpty)
        {
            return;
        }

        float uMax = (float)frame.Width / frame.CodedWidth;
        float vMax = (float)frame.Height / frame.CodedHeight;
        QuadTexCoords uv = VideoLayout.Corners(request.Rotation, uMax, vMax);
        YuvRange range = YuvRange.For(frame.PixelFormat == FramePixelFormat.P010);
        var constants = new FrameConstants
        {
            Range = new(range.LumaScale, range.LumaBias, range.ChromaScale, range.ChromaBias),
            Limits = new(
                (frame.Width - 0.5f) / frame.CodedWidth,
                (frame.Height - 0.5f) / frame.CodedHeight,
                (frame.Width - 1f) / frame.CodedWidth,
                (frame.Height - 1f) / frame.CodedHeight),
        };
        SetCorners(ref constants, rect, uv);
        Upload(constants);

        bool cached = frame.IsDecoderSurface;
        PlaneViews views = cached ? CachedViews(frame) : CreateViews(frame);
        try
        {
            ID3D11ShaderResourceView** srvs = stackalloc ID3D11ShaderResourceView*[2] { views.Luma, views.Chroma };
            _context->PSSetShader(_videoShader, null, 0);
            _context->PSSetShaderResources(0, 2, srvs);
            _context->Draw(4, 0);
            srvs[0] = null;
            srvs[1] = null;
            // Ninguna capa del pool queda enlazada al pipeline cuando el decodificador la reutilice.
            _context->PSSetShaderResources(0, 2, srvs);
        }
        finally
        {
            if (!cached)
            {
                views.Release();
            }
        }
    }

    private void DrawBar(double progress, bool highlighted)
    {
        int height = highlighted ? BarHeightHighlighted : BarHeight;
        int filled = (int)Math.Round(Math.Clamp(progress, 0, 1) * Width);
        _context->PSSetShader(_solidShader, null, 0);
        DrawSolid(new PixelRect(0, Height - height, Width, height), 0.16f, 0.16f, 0.16f);
        if (filled > 0)
        {
            DrawSolid(new PixelRect(0, Height - height, filled, height), 0.95f, 0.55f, 0.15f);
        }
    }

    private void DrawSolid(PixelRect rect, float r, float g, float b)
    {
        var constants = new FrameConstants { Color = new(r, g, b, 1) };
        SetCorners(ref constants, rect, VideoLayout.Corners(0, 1, 1));
        Upload(constants);
        _context->Draw(4, 0);
    }

    private void SetCorners(ref FrameConstants constants, PixelRect rect, QuadTexCoords uv)
    {
        float left = (2f * rect.X / Width) - 1f;
        float right = (2f * (rect.X + rect.Width) / Width) - 1f;
        float top = 1f - (2f * rect.Y / Height);
        float bottom = 1f - (2f * (rect.Y + rect.Height) / Height);
        constants.TopLeft = new(left, top, uv.TopLeft.U, uv.TopLeft.V);
        constants.TopRight = new(right, top, uv.TopRight.U, uv.TopRight.V);
        constants.BottomLeft = new(left, bottom, uv.BottomLeft.U, uv.BottomLeft.V);
        constants.BottomRight = new(right, bottom, uv.BottomRight.U, uv.BottomRight.V);
    }

    private void Upload(in FrameConstants constants)
    {
        D3D11_MAPPED_SUBRESOURCE mapped;
        Check(_context->Map((ID3D11Resource*)_constants, 0, D3D11_MAP.D3D11_MAP_WRITE_DISCARD, 0, &mapped), "Map(constantes)");
        *(FrameConstants*)mapped.pData = constants;
        _context->Unmap((ID3D11Resource*)_constants, 0);
    }

    private PlaneViews CachedViews(D3D11TexturePayload frame)
    {
        var key = (frame.Texture, frame.ArraySlice);
        if (_views.TryGetValue(key, out PlaneViews views))
        {
            return views;
        }

        if (_views.Count >= MaxCachedViews)
        {
            ReleaseFrameViews();
        }

        views = CreateViews(frame);
        _views.Add(key, views);
        return views;
    }

    private PlaneViews CreateViews(D3D11TexturePayload frame)
    {
        (DXGI_FORMAT luma, DXGI_FORMAT chroma) = PlaneFormats(frame.PixelFormat);
        var texture = (ID3D11Resource*)frame.Texture;
        ID3D11ShaderResourceView* lumaView = CreateSliceView(texture, luma, frame.ArraySlice);
        try
        {
            return new PlaneViews(lumaView, CreateSliceView(texture, chroma, frame.ArraySlice));
        }
        catch
        {
            lumaView->Release();
            throw;
        }
    }

    private ID3D11ShaderResourceView* CreateSliceView(ID3D11Resource* texture, DXGI_FORMAT format, int slice)
    {
        var desc = new D3D11_SHADER_RESOURCE_VIEW_DESC
        {
            Format = format,
            ViewDimension = D3D_SRV_DIMENSION.D3D11_SRV_DIMENSION_TEXTURE2DARRAY,
        };
        desc.Texture2DArray.MostDetailedMip = 0;
        desc.Texture2DArray.MipLevels = 1;
        desc.Texture2DArray.FirstArraySlice = (uint)slice;
        desc.Texture2DArray.ArraySize = 1;
        ID3D11ShaderResourceView* view;
        Check(_device->CreateShaderResourceView(texture, &desc, &view), "CreateShaderResourceView");
        return view;
    }

    private readonly struct PlaneViews(ID3D11ShaderResourceView* luma, ID3D11ShaderResourceView* chroma)
    {
        public ID3D11ShaderResourceView* Luma { get; } = luma;

        public ID3D11ShaderResourceView* Chroma { get; } = chroma;

        public void Release()
        {
            Luma->Release();
            Chroma->Release();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FrameConstants
    {
        public Float4 TopLeft;
        public Float4 TopRight;
        public Float4 BottomLeft;
        public Float4 BottomRight;
        public Float4 Range;
        public Float4 Limits;
        public Float4 Color;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Float4(float x, float y, float z, float w)
    {
        public readonly float X = x;
        public readonly float Y = y;
        public readonly float Z = z;
        public readonly float W = w;
    }
}
