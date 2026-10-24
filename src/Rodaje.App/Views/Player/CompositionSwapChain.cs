using Rodaje.Media.Gpu;
using Rodaje.Render;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.DirectX.DXGI;
using static TerraFX.Interop.Windows.Windows;

namespace Rodaje.App.Views.Player;

/// <summary>
/// Cadena de intercambio de composición (para un <c>SwapChainPanel</c>) flip-model con latencia de un cuadro, en
/// píxeles físicos: el búfer mide el panel por su escala de composición y una matriz inversa lo devuelve a unidades
/// del panel, así que la imagen no se reescala. Todo menos la creación del panel se usa desde el hilo de render.
/// </summary>
internal sealed unsafe partial class CompositionSwapChain : IDisposable
{
    private const uint SwapChainFlags = (uint)DXGI_SWAP_CHAIN_FLAG.DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT;

    private readonly GpuDevice _gpu;
    private readonly ID3D11Device* _device;
    private readonly ID3D11DeviceContext* _context;
    private IDXGISwapChain2* _swapChain;
    private ID3D11RenderTargetView* _target;
    private HANDLE _frameWait;

    /// <param name="tenBits">Búfer <c>R10G10B10A2</c> en vez de <c>B8G8R8A8</c> (<see cref="Services.Settings.ViewerBitDepth"/>).</param>
    public CompositionSwapChain(GpuDevice gpu, int width, int height, float scaleX, float scaleY, bool tenBits = false)
    {
        _gpu = gpu;
        _device = (ID3D11Device*)gpu.NativeDevice;
        _context = (ID3D11DeviceContext*)gpu.NativeContext;
        TenBits = tenBits;
        try
        {
            Create(Math.Max(1, width), Math.Max(1, height));
            SetScale(scaleX, scaleY);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary><c>IDXGISwapChain1*</c> para <c>ISwapChainPanelNative::SetSwapChain</c>.</summary>
    public nint NativeSwapChain => (nint)_swapChain;

    /// <summary>Se señala cuando la cadena admite otro cuadro; esperarla antes de pintar da la latencia mínima.</summary>
    public nint FrameWaitHandle => (nint)_frameWait.Value;

    public int Width { get; private set; }

    public int Height { get; private set; }

    public bool TenBits { get; }

    public float ScaleX { get; private set; } = 1;

    public float ScaleY { get; private set; } = 1;

    /// <summary>Cambia el tamaño del búfer (en píxeles físicos) y la escala de composición.</summary>
    public void Resize(int width, int height, float scaleX, float scaleY)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (width != Width || height != Height)
        {
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

        if (scaleX != ScaleX || scaleY != ScaleY)
        {
            SetScale(scaleX, scaleY);
        }
    }

    /// <summary>Pinta la composición ajustada con barras negras, o en <paramref name="placement"/> si se da (negro si no hay), y presenta con vsync.</summary>
    /// <param name="waitForVsync">
    /// <c>false</c> para una segunda ventana (otra pantalla): presenta sin esperar y, si la cadena sigue ocupada, se salta el cuadro
    /// en vez de frenar al reproductor.
    /// </param>
    public void Render(Presenter presenter, Compositor? composition, PixelRect? placement = null, bool waitForVsync = true)
    {
        if (composition is not null)
        {
            presenter.Draw((nint)_target, Width, Height, composition, placement);
        }
        else
        {
            _gpu.LockContext();
            try
            {
                float* black = stackalloc float[] { 0f, 0f, 0f, 1f };
                _context->ClearRenderTargetView(_target, black);
            }
            finally
            {
                _gpu.UnlockContext();
            }
        }

        HRESULT hr = waitForVsync ? _swapChain->Present(1, 0) : _swapChain->Present(0, DXGI_PRESENT_DO_NOT_WAIT);
        if (hr != DXGI_STATUS_OCCLUDED && hr != DXGI_ERROR_WAS_STILL_DRAWING)
        {
            Check(hr, "Present");
        }
    }

    public void Dispose()
    {
        Release(ref _target);
        Release(ref _swapChain);
        if (_frameWait != HANDLE.NULL)
        {
            CloseHandle(_frameWait);
            _frameWait = HANDLE.NULL;
        }
    }

    /// <summary>Bits por color del monitor que más tenga (<c>DXGI_OUTPUT_DESC1</c>); 8 si no hay monitores (WARP) o no se puede leer.</summary>
    /// <remarks>shortcut: mira todos los monitores del adaptador y no el que tiene la ventana; afinar si alguien mezcla uno de 10 bits con otro de 8.</remarks>
    public static int MaxBitsPerColor(GpuDevice gpu)
    {
        var device = (ID3D11Device*)gpu.NativeDevice;
        IDXGIDevice* dxgiDevice = null;
        IDXGIAdapter* adapter = null;
        int bits = 8;
        try
        {
            if (device->QueryInterface(__uuidof<IDXGIDevice>(), (void**)&dxgiDevice).FAILED || dxgiDevice->GetAdapter(&adapter).FAILED)
            {
                return bits;
            }

            for (uint i = 0; ; i++)
            {
                IDXGIOutput* output;
                if (adapter->EnumOutputs(i, &output).FAILED)
                {
                    return bits;
                }

                IDXGIOutput6* output6 = null;
                if (output->QueryInterface(__uuidof<IDXGIOutput6>(), (void**)&output6).SUCCEEDED)
                {
                    DXGI_OUTPUT_DESC1 desc;
                    if (output6->GetDesc1(&desc).SUCCEEDED)
                    {
                        bits = Math.Max(bits, (int)desc.BitsPerColor);
                    }
                }

                Release(ref output6);
                Release(ref output);
            }
        }
        finally
        {
            Release(ref adapter);
            Release(ref dxgiDevice);
        }
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
                ? " La GPU se reinició o se perdió (por ejemplo, al actualizar el controlador)."
                : "";
            throw new InvalidOperationException($"{operation} falló con HRESULT 0x{hr.Value:X8}.{hint}");
        }
    }

    private void SetScale(float scaleX, float scaleY)
    {
        if (!(scaleX > 0) || !(scaleY > 0))
        {
            return;
        }

        var matrix = new DXGI_MATRIX_3X2_F { _11 = 1f / scaleX, _22 = 1f / scaleY };
        Check(_swapChain->SetMatrixTransform(&matrix), "SetMatrixTransform");
        ScaleX = scaleX;
        ScaleY = scaleY;
    }

    private void Create(int width, int height)
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
                Width = (uint)width,
                Height = (uint)height,
                Format = TenBits ? DXGI_FORMAT.DXGI_FORMAT_R10G10B10A2_UNORM : DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
                SampleDesc = new DXGI_SAMPLE_DESC(1, 0),
                BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT,
                BufferCount = 2,
                // Las cadenas de composición solo admiten STRETCH.
                Scaling = DXGI_SCALING.DXGI_SCALING_STRETCH,
                SwapEffect = DXGI_SWAP_EFFECT.DXGI_SWAP_EFFECT_FLIP_DISCARD,
                AlphaMode = DXGI_ALPHA_MODE.DXGI_ALPHA_MODE_IGNORE,
                Flags = SwapChainFlags,
            };
            Check(factory->CreateSwapChainForComposition((IUnknown*)_device, &desc, null, &swapChain1), "CreateSwapChainForComposition");
            IDXGISwapChain2* swapChain;
            Check(swapChain1->QueryInterface(__uuidof<IDXGISwapChain2>(), (void**)&swapChain), "QueryInterface(IDXGISwapChain2)");
            _swapChain = swapChain;
            Check(_swapChain->SetMaximumFrameLatency(1), "SetMaximumFrameLatency");
            _frameWait = _swapChain->GetFrameLatencyWaitableObject();
            Width = width;
            Height = height;
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
}
