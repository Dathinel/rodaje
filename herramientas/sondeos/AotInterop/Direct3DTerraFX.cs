#if GRAFICOS_TERRAFX
using System.Diagnostics;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.DirectX.D3D11;
using static TerraFX.Interop.DirectX.DirectX;
using static TerraFX.Interop.Windows.Windows;

namespace AotInterop;

// Dispositivo D3D11 con TerraFX.Interop.Windows: punteros COM crudos, vtables como punteros a función.
internal sealed unsafe class Gpu : IDisposable
{
    private ID3D11Device* dispositivo;
    private ID3D11DeviceContext* contexto;

    public Gpu()
    {
        D3D_FEATURE_LEVEL* niveles = stackalloc D3D_FEATURE_LEVEL[] { D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_0 };
        ID3D11Device* d;
        ID3D11DeviceContext* c;
        D3D_FEATURE_LEVEL nivel;
        Comprobar(D3D11CreateDevice(null, D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_HARDWARE, HMODULE.NULL,
            (uint)(D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_VIDEO_SUPPORT | D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT),
            niveles, 2, D3D11_SDK_VERSION, &d, &nivel, &c), "D3D11CreateDevice");
        dispositivo = d;
        contexto = c;
    }

    private static void Comprobar(HRESULT hr, string que)
    {
        if (hr.FAILED)
            throw new InvalidOperationException($"{que}: HRESULT 0x{hr.Value:X8}");
    }

    public nint Dispositivo => (nint)dispositivo;

    public nint DispositivoConReferencia()
    {
        dispositivo->AddRef();
        return (nint)dispositivo;
    }

    public string Descripcion
    {
        get
        {
            IDXGIDevice* dxgi;
            Comprobar(dispositivo->QueryInterface(__uuidof<IDXGIDevice>(), (void**)&dxgi), "QueryInterface(IDXGIDevice)");
            IDXGIAdapter* adaptador;
            Comprobar(dxgi->GetAdapter(&adaptador), "GetAdapter");
            DXGI_ADAPTER_DESC d;
            Comprobar(adaptador->GetDesc(&d), "GetDesc");
            adaptador->Release();
            dxgi->Release();
            string nombre = new string((char*)&d.Description).TrimEnd('\0');
            return $"nivel {dispositivo->GetFeatureLevel()}, adaptador {nombre} ({(ulong)d.DedicatedVideoMemory / (1024 * 1024)} MB)";
        }
    }

    public static void Probar()
    {
        var reloj = Stopwatch.StartNew();
        using var gpu = new Gpu();
        Console.WriteLine($"D3D11 (TerraFX): dispositivo en {reloj.Elapsed.TotalMilliseconds:0.0} ms, {gpu.Descripcion}");
        gpu.LimpiarYLeer();
    }

    private static D3D11_TEXTURE2D_DESC Descriptor(DXGI_FORMAT formato, uint ancho, uint alto, uint bind, bool lectura) => new()
    {
        Width = ancho,
        Height = alto,
        MipLevels = 1,
        ArraySize = 1,
        Format = formato,
        SampleDesc = new DXGI_SAMPLE_DESC(1, 0),
        Usage = lectura ? D3D11_USAGE.D3D11_USAGE_STAGING : D3D11_USAGE.D3D11_USAGE_DEFAULT,
        BindFlags = bind,
        CPUAccessFlags = lectura ? (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ : 0,
    };

    private void LimpiarYLeer()
    {
        var desc = Descriptor(DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM, 1920, 1080,
            (uint)(D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET | D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE), false);
        ID3D11Texture2D* textura;
        Comprobar(dispositivo->CreateTexture2D(&desc, null, &textura), "CreateTexture2D");
        ID3D11RenderTargetView* vista;
        Comprobar(dispositivo->CreateRenderTargetView((ID3D11Resource*)textura, null, &vista), "CreateRenderTargetView");
        float* color = stackalloc float[] { 1f, 0.5f, 0.25f, 1f };
        contexto->ClearRenderTargetView(vista, color);

        var descLectura = Descriptor(DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM, 1920, 1080, 0, true);
        ID3D11Texture2D* lectura;
        Comprobar(dispositivo->CreateTexture2D(&descLectura, null, &lectura), "CreateTexture2D(staging)");
        contexto->CopyResource((ID3D11Resource*)lectura, (ID3D11Resource*)textura);
        D3D11_MAPPED_SUBRESOURCE m;
        Comprobar(contexto->Map((ID3D11Resource*)lectura, 0, D3D11_MAP.D3D11_MAP_READ, 0, &m), "Map");
        uint pixel = *(uint*)((byte*)m.pData + 540 * m.RowPitch + 960 * 4);
        contexto->Unmap((ID3D11Resource*)lectura, 0);
        lectura->Release();
        vista->Release();
        textura->Release();
        Console.WriteLine($"  textura 1920x1080 BGRA limpiada a (1, 0.5, 0.25) y leída: 0x{pixel:X8} (esperado 0xFFFF7F40/0xFFFF8040)");
    }

    public Lectura LeerPlanoY(nint texturaNativa, uint indice, int ancho, int alto)
    {
        var textura = (ID3D11Texture2D*)texturaNativa;
        D3D11_TEXTURE2D_DESC d;
        textura->GetDesc(&d);
        ID3D11Device* suDispositivo;
        textura->GetDevice(&suDispositivo);
        bool mismo = suDispositivo == dispositivo;
        suDispositivo->Release();

        var desc = Descriptor(d.Format, d.Width, d.Height, 0, true);
        ID3D11Texture2D* lectura;
        Comprobar(dispositivo->CreateTexture2D(&desc, null, &lectura), "CreateTexture2D(staging)");
        contexto->CopySubresourceRegion((ID3D11Resource*)lectura, 0, 0, 0, 0, (ID3D11Resource*)textura, indice, null);
        D3D11_MAPPED_SUBRESOURCE m;
        Comprobar(contexto->Map((ID3D11Resource*)lectura, 0, D3D11_MAP.D3D11_MAP_READ, 0, &m), "Map");
        var y = Lectura.CopiarPlanoY((byte*)m.pData, m.RowPitch, d.Format == DXGI_FORMAT.DXGI_FORMAT_P010, ancho, alto);
        contexto->Unmap((ID3D11Resource*)lectura, 0);
        lectura->Release();
        return new Lectura(y, $"{d.Format} {d.Width}x{d.Height} arreglo={d.ArraySize} bind=0x{d.BindFlags:X}", mismo);
    }

    public void Dispose()
    {
        if (contexto != null)
            contexto->Release();
        if (dispositivo != null)
            dispositivo->Release();
        contexto = null;
        dispositivo = null;
    }
}
#endif
