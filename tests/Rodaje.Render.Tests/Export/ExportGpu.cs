using Rodaje.Media.Export;
using Rodaje.Media.Gpu;
using Rodaje.Render.Export;
using Rodaje.Render.Shaders;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.DirectX.D3D11;
using static TerraFX.Interop.DirectX.DirectX;

namespace Rodaje.Render.Tests.Export;

/// <summary>
/// Dispositivo para las pruebas del conversor: WARP (el rasterizador de software de Windows, también en la CI) o la GPU
/// de verdad a través de <see cref="GpuDevice"/>.
/// </summary>
internal sealed unsafe class ExportGpu : IDisposable
{
    private ID3D11Device* _device;
    private ID3D11DeviceContext* _context;
    private readonly bool _owned;

    private ExportGpu(ID3D11Device* device, ID3D11DeviceContext* context, GpuDevice? gpu, bool owned)
    {
        _device = device;
        _context = context;
        Gpu = gpu;
        _owned = owned;
    }

    public GpuDevice? Gpu { get; }

    public nint Device => (nint)_device;

    public nint Context => (nint)_context;

    public static ExportGpu Warp()
    {
        ID3D11Device* device;
        ID3D11DeviceContext* context;
        D3D_FEATURE_LEVEL level;
        D3D_FEATURE_LEVEL wanted = D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_0;
        Check(D3D11CreateDevice(null, D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_WARP, HMODULE.NULL, 0, &wanted, 1, D3D11_SDK_VERSION,
            &device, &level, &context));
        return new ExportGpu(device, context, null, owned: true);
    }

    public static ExportGpu Hardware(GpuDevice gpu) =>
        new((ID3D11Device*)gpu.NativeDevice, (ID3D11DeviceContext*)gpu.NativeContext, gpu, owned: false);

    public ExportFrameConverter Converter(int sourceWidth, int sourceHeight, ExportVideoFormat output, ExportFit fit = ExportFit.Bars,
        ExportScaleFilter filter = ExportScaleFilter.Auto, int ring = ExportFrameConverter.DefaultRingSize, TimeProvider? time = null, bool dither = true) =>
        new(Gpu, Device, Context, sourceWidth, sourceHeight, output, fit, filter, ring, ShaderCache.Default, time ?? TimeProvider.System)
        {
            Dither = dither,
        };

    /// <summary>Sube una imagen RGBA16F con toda la cadena de mips (como la textura del compositor).</summary>
    public nint Upload(Picture picture)
    {
        int levels = 1 + (int)Math.Log2(Math.Max(picture.Width, picture.Height));
        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width = (uint)picture.Width,
            Height = (uint)picture.Height,
            MipLevels = (uint)levels,
            ArraySize = 1,
            Format = DXGI_FORMAT.DXGI_FORMAT_R16G16B16A16_FLOAT,
            SampleDesc = new DXGI_SAMPLE_DESC(1, 0),
            Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
            BindFlags = (uint)(D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET | D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE),
            MiscFlags = (uint)D3D11_RESOURCE_MISC_FLAG.D3D11_RESOURCE_MISC_GENERATE_MIPS,
        };
        ID3D11Texture2D* texture;
        Check(_device->CreateTexture2D(&desc, null, &texture));
        Half[] halves = picture.ToHalves();
        Gpu?.LockContext();
        try
        {
            fixed (Half* data = halves)
            {
                _context->UpdateSubresource((ID3D11Resource*)texture, 0, null, data, (uint)(picture.Width * 8), 0);
            }
        }
        finally
        {
            Gpu?.UnlockContext();
        }

        return (nint)texture;
    }

    public static void Release(nint texture)
    {
        if (texture != 0)
        {
            ((IUnknown*)texture)->Release();
        }
    }

    public void Dispose()
    {
        if (!_owned)
        {
            return;
        }

        if (_context != null)
        {
            _context->Release();
            _context = null;
        }

        if (_device != null)
        {
            _device->Release();
            _device = null;
        }
    }

    private static void Check(HRESULT hr)
    {
        if (hr.FAILED)
        {
            throw new InvalidOperationException($"Direct3D falló con 0x{hr.Value:X8}.");
        }
    }
}
