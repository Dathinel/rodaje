#if !GRAFICOS_TERRAFX
using System.Diagnostics;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace AotInterop;

// Dispositivo D3D11 con Vortice (SharpGen.Runtime).
internal sealed class Gpu : IDisposable
{
    private readonly ID3D11Device dispositivo;
    private readonly ID3D11DeviceContext contexto;

    // VideoSupport es obligatorio para que FFmpeg pueda obtener ID3D11VideoDevice del dispositivo.
    public Gpu()
    {
        D3D11.D3D11CreateDevice(null, DriverType.Hardware,
            DeviceCreationFlags.VideoSupport | DeviceCreationFlags.BgraSupport,
            [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0],
            out ID3D11Device? d, out FeatureLevel _, out ID3D11DeviceContext? c).CheckError();
        dispositivo = d!;
        contexto = c!;
    }

    public nint Dispositivo => dispositivo.NativePointer;

    public nint DispositivoConReferencia()
    {
        dispositivo.AddRef();
        return dispositivo.NativePointer;
    }

    public string Descripcion
    {
        get
        {
            using var dxgi = dispositivo.QueryInterface<IDXGIDevice>();
            using IDXGIAdapter adaptador = dxgi.GetAdapter();
            var d = adaptador.Description;
            return $"nivel {dispositivo.FeatureLevel}, adaptador {d.Description} ({d.DedicatedVideoMemory / (1024 * 1024)} MB)";
        }
    }

    public static void Probar()
    {
        var reloj = Stopwatch.StartNew();
        using var gpu = new Gpu();
        Console.WriteLine($"D3D11 (Vortice): dispositivo en {reloj.Elapsed.TotalMilliseconds:0.0} ms, {gpu.Descripcion}");
        gpu.LimpiarYLeer();
    }

    private void LimpiarYLeer()
    {
        using ID3D11Texture2D textura = dispositivo.CreateTexture2D(new Texture2DDescription(
            Format.B8G8R8A8_UNorm, 1920, 1080, 1, 1, BindFlags.RenderTarget | BindFlags.ShaderResource));
        using ID3D11RenderTargetView vista = dispositivo.CreateRenderTargetView(textura);
        contexto.ClearRenderTargetView(vista, new Color4(1f, 0.5f, 0.25f, 1f));

        using ID3D11Texture2D lectura = dispositivo.CreateTexture2D(new Texture2DDescription(
            Format.B8G8R8A8_UNorm, 1920, 1080, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
        contexto.CopyResource(lectura, textura);
        MappedSubresource m = contexto.Map(lectura, 0, MapMode.Read);
        uint pixel = (uint)Marshal.ReadInt32(m.DataPointer + (nint)(540 * m.RowPitch + 960 * 4));
        contexto.Unmap(lectura, 0);
        Console.WriteLine($"  textura 1920x1080 BGRA limpiada a (1, 0.5, 0.25) y leída: 0x{pixel:X8} (esperado 0xFFFF7F40/0xFFFF8040)");
    }

    // Copia la rebanada `indice` del arreglo de texturas del decodificador a una textura de lectura y saca el plano Y.
    public unsafe Lectura LeerPlanoY(nint texturaNativa, uint indice, int ancho, int alto)
    {
        // Envoltorio sin AddRef: no se libera, la textura es de FFmpeg.
        var textura = new ID3D11Texture2D(texturaNativa);
        Texture2DDescription d = textura.Description;
        bool mismo;
        using (ID3D11Device suDispositivo = textura.Device)
            mismo = suDispositivo.NativePointer == dispositivo.NativePointer;

        using ID3D11Texture2D lectura = dispositivo.CreateTexture2D(new Texture2DDescription(d.Format, d.Width, d.Height, 1, 1,
            BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
        contexto.CopySubresourceRegion(lectura, 0, 0, 0, 0, textura, indice);
        MappedSubresource m = contexto.Map(lectura, 0, MapMode.Read);
        var y = Lectura.CopiarPlanoY((byte*)m.DataPointer, m.RowPitch, d.Format == Format.P010, ancho, alto);
        contexto.Unmap(lectura, 0);
        return new Lectura(y, $"{d.Format} {d.Width}x{d.Height} arreglo={d.ArraySize} bind={d.BindFlags}", mismo);
    }

    public void Dispose()
    {
        contexto.Dispose();
        dispositivo.Dispose();
    }
}
#endif
