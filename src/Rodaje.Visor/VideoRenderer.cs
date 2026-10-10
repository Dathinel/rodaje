using System.Runtime.InteropServices;
using Rodaje.Media.Gpu;
using Rodaje.Render;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.DirectX.D3D11;
using static TerraFX.Interop.DirectX.DXGI;
using static TerraFX.Interop.Windows.Windows;

namespace Rodaje.Visor;

/// <summary>Qué pintar en un refresco además de la composición.</summary>
/// <param name="Progress">Posición entre 0 y 1, para la barra.</param>
internal readonly record struct RenderRequest(double Progress, bool ShowBar, bool BarHighlighted);

/// <summary>
/// Cadena de intercambio flip-model con latencia de un cuadro: el <see cref="Presenter"/> pinta la composición
/// ajustada con barras negras y encima va la barra de progreso.
/// </summary>
internal sealed unsafe class VideoRenderer : IDisposable
{
    public const int BarHeight = 5;
    public const int BarHeightHighlighted = 10;

    private const uint SwapChainFlags = (uint)DXGI_SWAP_CHAIN_FLAG.DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT;

    private readonly GpuDevice _gpu;
    private readonly ID3D11Device* _device;
    private readonly ID3D11DeviceContext* _context;
    private readonly Presenter _presenter;
    private IDXGISwapChain2* _swapChain;
    private ID3D11RenderTargetView* _target;
    private ID3D11VertexShader* _vertexShader;
    private ID3D11PixelShader* _solidShader;
    private ID3D11Buffer* _constants;
    private HANDLE _frameWait;

    public VideoRenderer(GpuDevice gpu, HWND window)
    {
        _gpu = gpu;
        _device = (ID3D11Device*)gpu.NativeDevice;
        _context = (ID3D11DeviceContext*)gpu.NativeContext;
        _presenter = new Presenter(gpu);
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
            Release(ref _target);
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

    /// <summary>Pinta la composición (o negro si no hay) y presenta con vsync. Devuelve <c>false</c> si la ventana está tapada.</summary>
    public bool Render(Compositor? composition, in RenderRequest request)
    {
        if (composition is not null)
        {
            _presenter.Draw((nint)_target, Width, Height, composition);
        }

        _gpu.LockContext();
        try
        {
            if (composition is null)
            {
                float* black = stackalloc float[] { 0f, 0f, 0f, 1f };
                _context->ClearRenderTargetView(_target, black);
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
        Release(ref _target);
        Release(ref _constants);
        Release(ref _vertexShader);
        Release(ref _solidShader);
        Release(ref _swapChain);
        _presenter.Dispose();
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

    private void CreatePipeline()
    {
        byte[] vs = Shaders.VertexShader();
        byte[] solid = Shaders.SolidPixelShader();
        fixed (byte* v = vs, s = solid)
        {
            ID3D11VertexShader* vertex;
            Check(_device->CreateVertexShader(v, (nuint)vs.Length, null, &vertex), "CreateVertexShader");
            _vertexShader = vertex;
            ID3D11PixelShader* pixel;
            Check(_device->CreatePixelShader(s, (nuint)solid.Length, null, &pixel), "CreatePixelShader(barra)");
            _solidShader = pixel;
        }

        var bufferDesc = new D3D11_BUFFER_DESC
        {
            ByteWidth = (uint)sizeof(BarConstants),
            Usage = D3D11_USAGE.D3D11_USAGE_DYNAMIC,
            BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_CONSTANT_BUFFER,
            CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_WRITE,
        };
        ID3D11Buffer* constants;
        Check(_device->CreateBuffer(&bufferDesc, null, &constants), "CreateBuffer");
        _constants = constants;
    }

    private void DrawBar(double progress, bool highlighted)
    {
        ID3D11RenderTargetView* target = _target;
        _context->OMSetRenderTargets(1, &target, null);
        _context->OMSetBlendState(null, null, uint.MaxValue);
        _context->RSSetState(null);
        var viewport = new D3D11_VIEWPORT { Width = Width, Height = Height, MaxDepth = 1 };
        _context->RSSetViewports(1, &viewport);
        _context->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY.D3D_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP);
        _context->IASetInputLayout(null);
        _context->VSSetShader(_vertexShader, null, 0);
        _context->PSSetShader(_solidShader, null, 0);
        ID3D11Buffer* constants = _constants;
        _context->VSSetConstantBuffers(0, 1, &constants);
        _context->PSSetConstantBuffers(0, 1, &constants);

        int height = highlighted ? BarHeightHighlighted : BarHeight;
        int filled = (int)Math.Round(Math.Clamp(progress, 0, 1) * Width);
        DrawSolid(new PixelRect(0, Height - height, Width, height), 0.16f, 0.16f, 0.16f);
        if (filled > 0)
        {
            DrawSolid(new PixelRect(0, Height - height, filled, height), 0.95f, 0.55f, 0.15f);
        }

        _context->OMSetRenderTargets(0, null, null);
    }

    private void DrawSolid(PixelRect rect, float r, float g, float b)
    {
        float left = (2f * rect.X / Width) - 1f;
        float right = (2f * (rect.X + rect.Width) / Width) - 1f;
        float top = 1f - (2f * rect.Y / Height);
        float bottom = 1f - (2f * (rect.Y + rect.Height) / Height);
        var constants = new BarConstants
        {
            TopLeft = new(left, top),
            TopRight = new(right, top),
            BottomLeft = new(left, bottom),
            BottomRight = new(right, bottom),
            Color = new(r, g, b, 1),
        };
        D3D11_MAPPED_SUBRESOURCE mapped;
        Check(_context->Map((ID3D11Resource*)_constants, 0, D3D11_MAP.D3D11_MAP_WRITE_DISCARD, 0, &mapped), "Map(constantes)");
        *(BarConstants*)mapped.pData = constants;
        _context->Unmap((ID3D11Resource*)_constants, 0);
        _context->Draw(4, 0);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BarConstants
    {
        public Float4 TopLeft;
        public Float4 TopRight;
        public Float4 BottomLeft;
        public Float4 BottomRight;
        public Float4 Color;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Float4(float x, float y, float z = 0, float w = 0)
    {
        public readonly float X = x;
        public readonly float Y = y;
        public readonly float Z = z;
        public readonly float W = w;
    }
}
