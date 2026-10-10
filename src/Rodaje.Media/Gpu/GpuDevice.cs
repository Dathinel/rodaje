using System.Globalization;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.DirectX.D3D11;
using static TerraFX.Interop.DirectX.DirectX;
using static TerraFX.Interop.Windows.Windows;
using FF = FFmpeg.AutoGen.Abstractions;

namespace Rodaje.Media.Gpu;

/// <summary>Dispositivo Direct3D 11 de Rodaje, compartido con el decodificador D3D11VA de FFmpeg.</summary>
/// <remarks>
/// Tiene la protección multihilo activada, así que cada llamada al contexto inmediato es atómica. Las
/// secuencias de varias llamadas (copiar y luego <c>Map</c>) van dentro del candado del contexto, que es
/// el mismo que usa FFmpeg al decodificar en otro hilo.
/// </remarks>
public sealed unsafe class GpuDevice : IDisposable
{
    private readonly Lock _managedLock = new();
    private ID3D11Device* _device;
    private ID3D11DeviceContext* _context;
    private FF.AVBufferRef* _hardwareDevice;
    private delegate* unmanaged[Cdecl]<void*, void> _lock;
    private delegate* unmanaged[Cdecl]<void*, void> _unlock;
    private void* _lockContext;

    private GpuDevice(ID3D11Device* device, ID3D11DeviceContext* context, bool videoSupport)
    {
        _device = device;
        _context = context;
        HasVideoSupport = videoSupport;
        EnableMultithreadProtection();
        (AdapterName, DedicatedVideoMemory) = ReadAdapter();
        FeatureLevel = FormatFeatureLevel(device->GetFeatureLevel());
        SupportsNv12Textures = SupportsShaderTexture(DXGI_FORMAT.DXGI_FORMAT_NV12);
        SupportsP010Textures = SupportsShaderTexture(DXGI_FORMAT.DXGI_FORMAT_P010);
        if (videoSupport && FfmpegLibrary.TryInitialize(out _))
        {
            CreateHardwareDeviceContext();
        }
    }

    /// <summary>El dispositivo se creó con <c>D3D11_CREATE_DEVICE_VIDEO_SUPPORT</c>.</summary>
    public bool HasVideoSupport { get; }

    /// <summary>FFmpeg puede decodificar con D3D11VA sobre este dispositivo.</summary>
    public bool SupportsHardwareDecoding => _hardwareDevice != null;

    public bool SupportsNv12Textures { get; }

    public bool SupportsP010Textures { get; }

    public string AdapterName { get; }

    public string FeatureLevel { get; }

    /// <summary>VRAM propia del adaptador en bytes; 0 si no se pudo leer (o la GPU es integrada y no declara).</summary>
    public long DedicatedVideoMemory { get; }

    public bool IsDisposed => _device == null;

    /// <summary><c>ID3D11Device*</c> para el renderizador, sin referencia propia: vale hasta <see cref="Dispose"/>.</summary>
    public nint NativeDevice => (nint)Device;

    /// <summary>
    /// <c>ID3D11DeviceContext*</c> inmediato, compartido con el decodificador. Las secuencias de llamadas que
    /// dependen del estado del pipeline van entre <see cref="LockContext"/> y <see cref="UnlockContext"/>.
    /// </summary>
    public nint NativeContext
    {
        get
        {
            ObjectDisposedException.ThrowIf(_context == null, this);
            return (nint)_context;
        }
    }

    internal FF.AVBufferRef* HardwareDeviceContext => _hardwareDevice;

    internal ID3D11Device* Device
    {
        get
        {
            ObjectDisposedException.ThrowIf(_device == null, this);
            return _device;
        }
    }

    /// <summary>Crea el dispositivo sobre el adaptador de hardware por defecto.</summary>
    /// <exception cref="MediaException">No hay Direct3D 11 por hardware.</exception>
    public static GpuDevice Create() =>
        TryCreate(out string? reason) ?? throw new MediaException(reason!);

    /// <summary>Devuelve <c>null</c> y el motivo cuando no hay hardware D3D11; entonces se decodifica en la CPU.</summary>
    public static GpuDevice? TryCreate(out string? failureReason)
    {
        uint baseFlags = (uint)D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT;
        uint videoFlag = (uint)D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_VIDEO_SUPPORT;
        HRESULT hr = CreateHardwareDevice(baseFlags | videoFlag, out ID3D11Device* device, out ID3D11DeviceContext* context);
        bool video = hr.SUCCEEDED;
        if (!video)
        {
            hr = CreateHardwareDevice(baseFlags, out device, out context);
        }

        if (hr.FAILED)
        {
            failureReason = string.Create(CultureInfo.InvariantCulture,
                $"No hay Direct3D 11 por hardware (D3D11CreateDevice devolvió 0x{hr.Value:X8}); se decodificará en la CPU.");
            return null;
        }

        failureReason = null;
        try
        {
            return new GpuDevice(device, context, video);
        }
        catch
        {
            context->Release();
            device->Release();
            throw;
        }
    }

    public override string ToString() =>
        $"{AdapterName} (nivel {FeatureLevel}{(SupportsHardwareDecoding ? ", D3D11VA" : ", sin D3D11VA")})";

    public void Dispose()
    {
        if (_device == null)
        {
            return;
        }

        FF.AVBufferRef* hw = _hardwareDevice;
        _hardwareDevice = null;
        if (hw != null)
        {
            FF.ffmpeg.av_buffer_unref(&hw);
        }

        _context->Release();
        _device->Release();
        _context = null;
        _device = null;
    }

    /// <summary>Entra al candado del contexto inmediato (recursivo). Siempre en pareja con <see cref="UnlockContext"/>.</summary>
    public void LockContext()
    {
        if (_lock != null)
        {
            _lock(_lockContext);
        }
        else
        {
            _managedLock.Enter();
        }
    }

    public void UnlockContext()
    {
        if (_unlock != null)
        {
            _unlock(_lockContext);
        }
        else
        {
            _managedLock.Exit();
        }
    }

    /// <summary>Referencias vivas del <c>ID3D11Device</c>, para detectar fugas en las pruebas.</summary>
    internal uint GetDeviceReferenceCount()
    {
        ID3D11Device* device = Device;
        device->AddRef();
        return device->Release();
    }

    /// <summary>Textura con contenido inicial, muestreable desde un shader. Devuelve una referencia propia.</summary>
    internal ID3D11Texture2D* CreateTexture(DXGI_FORMAT format, uint width, uint height, void* data, uint rowPitch)
    {
        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = format,
            SampleDesc = new DXGI_SAMPLE_DESC(1, 0),
            Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
            BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE,
        };
        var initial = new D3D11_SUBRESOURCE_DATA { pSysMem = data, SysMemPitch = rowPitch };
        ID3D11Texture2D* texture;
        Check(Device->CreateTexture2D(&desc, &initial, &texture), "CreateTexture2D");
        return texture;
    }

    /// <summary>Copia el plano Y de una capa de la textura a la CPU, en 8 bits (P010 se reduce con <c>&gt;&gt; 8</c>).</summary>
    internal byte[] ReadLuma(ID3D11Texture2D* texture, uint arraySlice, int width, int height)
    {
        D3D11_TEXTURE2D_DESC source;
        texture->GetDesc(&source);
        bool tenBit = source.Format == DXGI_FORMAT.DXGI_FORMAT_P010;
        if (!tenBit && source.Format != DXGI_FORMAT.DXGI_FORMAT_NV12)
        {
            throw new NotSupportedException($"La lectura de luma no admite el formato {source.Format}.");
        }

        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width = source.Width,
            Height = source.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = source.Format,
            SampleDesc = new DXGI_SAMPLE_DESC(1, 0),
            Usage = D3D11_USAGE.D3D11_USAGE_STAGING,
            CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ,
        };
        ID3D11Texture2D* staging;
        Check(Device->CreateTexture2D(&desc, null, &staging), "CreateTexture2D(staging)");
        var luma = new byte[width * height];
        try
        {
            LockContext();
            try
            {
                uint subresource = D3D11CalcSubresource(0, arraySlice, source.MipLevels);
                _context->CopySubresourceRegion((ID3D11Resource*)staging, 0, 0, 0, 0, (ID3D11Resource*)texture, subresource, null);
                D3D11_MAPPED_SUBRESOURCE mapped;
                Check(_context->Map((ID3D11Resource*)staging, 0, D3D11_MAP.D3D11_MAP_READ, 0, &mapped), "Map");
                try
                {
                    for (int y = 0; y < height; y++)
                    {
                        byte* row = (byte*)mapped.pData + (nuint)y * mapped.RowPitch;
                        Span<byte> target = luma.AsSpan(y * width, width);
                        if (tenBit)
                        {
                            for (int x = 0; x < width; x++)
                            {
                                target[x] = (byte)(((ushort*)row)[x] >> 8);
                            }
                        }
                        else
                        {
                            new ReadOnlySpan<byte>(row, width).CopyTo(target);
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
                UnlockContext();
            }
        }
        finally
        {
            staging->Release();
        }

        return luma;
    }

    internal static void Check(HRESULT hr, string operation)
    {
        if (hr.FAILED)
        {
            throw new MediaException(string.Create(CultureInfo.InvariantCulture,
                $"{operation} falló con HRESULT 0x{hr.Value:X8}."));
        }
    }

    private static HRESULT CreateHardwareDevice(uint flags, out ID3D11Device* device, out ID3D11DeviceContext* context)
    {
        D3D_FEATURE_LEVEL* levels = stackalloc D3D_FEATURE_LEVEL[]
        {
            D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_1,
            D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_0,
        };
        ID3D11Device* d = null;
        ID3D11DeviceContext* c = null;
        D3D_FEATURE_LEVEL level;
        HRESULT hr = D3D11CreateDevice(null, D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_HARDWARE, HMODULE.NULL, flags,
            levels, 2, D3D11_SDK_VERSION, &d, &level, &c);
        device = d;
        context = c;
        return hr;
    }

    private static string FormatFeatureLevel(D3D_FEATURE_LEVEL level) =>
        string.Create(CultureInfo.InvariantCulture, $"{(int)level >> 12}_{((int)level >> 8) & 0xF}");

    private void EnableMultithreadProtection()
    {
        ID3D11Multithread* multithread;
        Check(_device->QueryInterface(__uuidof<ID3D11Multithread>(), (void**)&multithread), "QueryInterface(ID3D11Multithread)");
        multithread->SetMultithreadProtected(true);
        multithread->Release();
    }

    private bool SupportsShaderTexture(DXGI_FORMAT format)
    {
        uint support;
        if (_device->CheckFormatSupport(format, &support).FAILED)
        {
            return false;
        }

        uint needed = (uint)(D3D11_FORMAT_SUPPORT.D3D11_FORMAT_SUPPORT_TEXTURE2D | D3D11_FORMAT_SUPPORT.D3D11_FORMAT_SUPPORT_SHADER_SAMPLE);
        return (support & needed) == needed;
    }

    /// <summary>
    /// VRAM local que usa este proceso ahora, según el presupuesto de DXGI (<c>QueryVideoMemoryInfo</c>); incluye
    /// lo de cualquier otro dispositivo del proceso. <c>null</c> si el sistema no lo informa.
    /// </summary>
    public long? QueryLocalVideoMemoryUsage()
    {
        ObjectDisposedException.ThrowIf(_device == null, this);
        IDXGIAdapter* adapter = GetAdapter();
        if (adapter == null)
        {
            return null;
        }

        try
        {
            IDXGIAdapter3* adapter3;
            if (adapter->QueryInterface(__uuidof<IDXGIAdapter3>(), (void**)&adapter3).FAILED)
            {
                return null;
            }

            DXGI_QUERY_VIDEO_MEMORY_INFO info;
            HRESULT hr = adapter3->QueryVideoMemoryInfo(0, DXGI_MEMORY_SEGMENT_GROUP.DXGI_MEMORY_SEGMENT_GROUP_LOCAL, &info);
            adapter3->Release();
            return hr.SUCCEEDED ? (long)info.CurrentUsage : null;
        }
        finally
        {
            adapter->Release();
        }
    }

    private IDXGIAdapter* GetAdapter()
    {
        IDXGIDevice* dxgi;
        if (_device->QueryInterface(__uuidof<IDXGIDevice>(), (void**)&dxgi).FAILED)
        {
            return null;
        }

        IDXGIAdapter* adapter;
        HRESULT hr = dxgi->GetAdapter(&adapter);
        dxgi->Release();
        return hr.SUCCEEDED ? adapter : null;
    }

    private (string Name, long DedicatedVideoMemory) ReadAdapter()
    {
        IDXGIAdapter* adapter = GetAdapter();
        if (adapter == null)
        {
            return ("desconocido", 0);
        }

        DXGI_ADAPTER_DESC desc;
        HRESULT hr = adapter->GetDesc(&desc);
        adapter->Release();
        return hr.SUCCEEDED
            ? (new string((char*)&desc.Description).TrimEnd('\0'), (long)desc.DedicatedVideoMemory)
            : ("desconocido", 0);
    }

    // Receta del sondeo: FFmpeg recibe NUESTRO dispositivo, así sus cuadros son texturas que el
    // renderizador puede muestrear sin copiarlas entre dispositivos.
    private void CreateHardwareDeviceContext()
    {
        FF.AVBufferRef* hw = FF.ffmpeg.av_hwdevice_ctx_alloc(FF.AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA);
        if (hw == null)
        {
            return;
        }

        var d3d = (FF.AVD3D11VADeviceContext*)((FF.AVHWDeviceContext*)hw->data)->hwctx;
        // FFmpeg libera el dispositivo con Release al destruir el contexto: se le cede una referencia propia.
        _device->AddRef();
        *(nint*)&d3d->device = (nint)_device;
        if (FF.ffmpeg.av_hwdevice_ctx_init(hw) < 0)
        {
            FF.ffmpeg.av_buffer_unref(&hw);
            return;
        }

        _lock = (delegate* unmanaged[Cdecl]<void*, void>)d3d->@lock.Pointer;
        _unlock = (delegate* unmanaged[Cdecl]<void*, void>)d3d->unlock.Pointer;
        _lockContext = d3d->lock_ctx;
        _hardwareDevice = hw;
    }
}
