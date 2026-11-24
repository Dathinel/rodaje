using Rodaje.Media.Gpu;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.DirectX.DXGI;
using static TerraFX.Interop.Windows.Windows;
using static Rodaje.Render.RenderException;

namespace Rodaje.Render.Gpu.D3D11;

/// <summary>Las vistas por plano y porción del arreglo de D3D11VA (<see cref="PlaneViewCache"/>).</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows7.0")]
internal sealed unsafe class D3D11SurfaceCache(D3D11Gpu gpu, TimeProvider? time) : GpuSurfaceCache
{
    public PlaneViewCache Views { get; } = new(gpu.NativeDevice, time);

    public override int Count => Views.Count;

    public override void EndFrame() => Views.EndFrame();

    public override void Trim() => Views.Trim();

    public override void Clear() => Views.Clear();

    protected override void Release() => Views.Dispose();
}

/// <summary>Cadena de intercambio para composición (<c>SwapChainPanel</c>), de dos búferes con <c>FLIP_DISCARD</c>.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows7.0")]
internal sealed unsafe class D3D11Swapchain : IGpuSwapchain
{
    private readonly D3D11Gpu _gpu;
    private IDXGISwapChain1* _swapChain;
    private GpuTexture? _backBuffer;

    public D3D11Swapchain(D3D11Gpu gpu, int width, int height, GpuFormat format)
    {
        _gpu = gpu;
        Format = format;
        IDXGIDevice* dxgiDevice = null;
        IDXGIAdapter* adapter = null;
        IDXGIFactory2* factory = null;
        try
        {
            ID3D11Device* device = gpu.NativeDevice;
            Check(device->QueryInterface(__uuidof<IDXGIDevice>(), (void**)&dxgiDevice), "QueryInterface(IDXGIDevice)");
            Check(dxgiDevice->GetAdapter(&adapter), "GetAdapter");
            Check(adapter->GetParent(__uuidof<IDXGIFactory2>(), (void**)&factory), "GetParent(IDXGIFactory2)");
            var desc = new DXGI_SWAP_CHAIN_DESC1
            {
                Width = (uint)width,
                Height = (uint)height,
                Format = D3D11Gpu.Native(format),
                SampleDesc = new DXGI_SAMPLE_DESC(1, 0),
                BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT,
                BufferCount = 2,
                Scaling = DXGI_SCALING.DXGI_SCALING_STRETCH,
                SwapEffect = DXGI_SWAP_EFFECT.DXGI_SWAP_EFFECT_FLIP_DISCARD,
                AlphaMode = DXGI_ALPHA_MODE.DXGI_ALPHA_MODE_IGNORE,
            };
            IDXGISwapChain1* swapChain;
            Check(factory->CreateSwapChainForComposition((IUnknown*)device, &desc, null, &swapChain), "CreateSwapChainForComposition");
            _swapChain = swapChain;
            Width = width;
            Height = height;
        }
        finally
        {
            Com.Release(ref factory);
            Com.Release(ref adapter);
            Com.Release(ref dxgiDevice);
        }
    }

    public int Width { get; private set; }

    public int Height { get; private set; }

    public GpuFormat Format { get; }

    /// <summary><c>IDXGISwapChain1*</c>, para dárselo al <c>SwapChainPanel</c>.</summary>
    public nint Native => (nint)_swapChain;

    public GpuTexture AcquireBackBuffer(IGpuFrame frame)
    {
        ObjectDisposedException.ThrowIf(_swapChain == null, this);
        if (_backBuffer is not null)
        {
            return _backBuffer;
        }

        ID3D11Texture2D* buffer;
        Check(_swapChain->GetBuffer(0, __uuidof<ID3D11Texture2D>(), (void**)&buffer), "GetBuffer");
        ID3D11RenderTargetView* rtv = null;
        try
        {
            Check(_gpu.NativeDevice->CreateRenderTargetView((ID3D11Resource*)buffer, null, &rtv), "CreateRenderTargetView");
            return _backBuffer = _gpu.Wrap(buffer, new GpuTextureDesc(Width, Height, Format, GpuTextureUsage.RenderTarget), rtv: rtv);
        }
        finally
        {
            Com.Release(ref rtv);
            buffer->Release();
        }
    }

    public void Resize(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        DropBackBuffer();
        _gpu.Device.LockContext();
        try
        {
            // El contexto puede retener el búfer como destino: ResizeBuffers falla si queda una referencia.
            _gpu.NativeContextUnbindTargets();
            Check(_swapChain->ResizeBuffers(0, (uint)width, (uint)height, DXGI_FORMAT.DXGI_FORMAT_UNKNOWN, 0), "ResizeBuffers");
        }
        finally
        {
            _gpu.Device.UnlockContext();
        }

        Width = width;
        Height = height;
    }

    public bool Present(bool vsync)
    {
        ObjectDisposedException.ThrowIf(_swapChain == null, this);
        DropBackBuffer();
        _gpu.Device.LockContext();
        try
        {
            HRESULT hr = vsync ? _swapChain->Present(1, 0) : _swapChain->Present(0, DXGI_PRESENT_DO_NOT_WAIT);
            if (hr == DXGI_ERROR_WAS_STILL_DRAWING || hr == DXGI_STATUS_OCCLUDED)
            {
                return false;
            }

            Check(hr, "Present");
            return true;
        }
        finally
        {
            _gpu.Device.UnlockContext();
        }
    }

    public void Dispose()
    {
        DropBackBuffer();
        Com.Release(ref _swapChain);
    }

    private void DropBackBuffer()
    {
        _backBuffer?.Dispose();
        _backBuffer = null;
    }
}
