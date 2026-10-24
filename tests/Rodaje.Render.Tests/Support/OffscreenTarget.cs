using Rodaje.Media.Gpu;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace Rodaje.Render.Tests.Support;

/// <summary>Textura BGRA8 (o R10G10B10A2) con vista de destino, como el búfer de una cadena de intercambio, que se puede leer.</summary>
internal sealed unsafe class OffscreenTarget : IDisposable
{
    private readonly GpuDevice _gpu;
    private readonly DXGI_FORMAT _format;
    private ID3D11Texture2D* _texture;
    private ID3D11RenderTargetView* _view;

    public OffscreenTarget(GpuDevice gpu, int width, int height, DXGI_FORMAT format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM)
    {
        _gpu = gpu;
        _format = format;
        Width = width;
        Height = height;
        var device = (ID3D11Device*)gpu.NativeDevice;
        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = format,
            SampleDesc = new DXGI_SAMPLE_DESC(1, 0),
            Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
            BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET,
        };
        ID3D11Texture2D* texture;
        Check(device->CreateTexture2D(&desc, null, &texture));
        _texture = texture;
        ID3D11RenderTargetView* view;
        Check(device->CreateRenderTargetView((ID3D11Resource*)texture, null, &view));
        _view = view;
    }

    public int Width { get; }

    public int Height { get; }

    public nint RenderTargetView => (nint)_view;

    /// <summary>Lee el contenido y lo devuelve en RGBA de 8 bits (los 10 bits de R10G10B10A2 se reducen con <c>&gt;&gt; 2</c>).</summary>
    public RgbaImage Read()
    {
        var device = (ID3D11Device*)_gpu.NativeDevice;
        var context = (ID3D11DeviceContext*)_gpu.NativeContext;
        D3D11_TEXTURE2D_DESC desc;
        _texture->GetDesc(&desc);
        desc.Usage = D3D11_USAGE.D3D11_USAGE_STAGING;
        desc.BindFlags = 0;
        desc.CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ;
        ID3D11Texture2D* staging;
        Check(device->CreateTexture2D(&desc, null, &staging));
        var pixels = new byte[Width * Height * 4];
        _gpu.LockContext();
        try
        {
            context->CopyResource((ID3D11Resource*)staging, (ID3D11Resource*)_texture);
            D3D11_MAPPED_SUBRESOURCE mapped;
            Check(context->Map((ID3D11Resource*)staging, 0, D3D11_MAP.D3D11_MAP_READ, 0, &mapped));
            for (int y = 0; y < Height; y++)
            {
                byte* row = (byte*)mapped.pData + ((nuint)y * mapped.RowPitch);
                for (int x = 0; x < Width; x++)
                {
                    int i = ((y * Width) + x) * 4;
                    if (_format == DXGI_FORMAT.DXGI_FORMAT_R10G10B10A2_UNORM)
                    {
                        uint packed = ((uint*)row)[x];
                        pixels[i] = (byte)((packed & 0x3FF) >> 2);
                        pixels[i + 1] = (byte)(((packed >> 10) & 0x3FF) >> 2);
                        pixels[i + 2] = (byte)(((packed >> 20) & 0x3FF) >> 2);
                        pixels[i + 3] = (byte)((packed >> 30) * 85);
                        continue;
                    }

                    pixels[i] = row[(x * 4) + 2];
                    pixels[i + 1] = row[(x * 4) + 1];
                    pixels[i + 2] = row[x * 4];
                    pixels[i + 3] = row[(x * 4) + 3];
                }
            }

            context->Unmap((ID3D11Resource*)staging, 0);
        }
        finally
        {
            _gpu.UnlockContext();
            staging->Release();
        }

        return new RgbaImage(pixels, Width, Height);
    }

    public void Dispose()
    {
        if (_view != null)
        {
            _view->Release();
            _view = null;
        }

        if (_texture != null)
        {
            _texture->Release();
            _texture = null;
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
