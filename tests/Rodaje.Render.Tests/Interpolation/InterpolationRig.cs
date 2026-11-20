using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Rodaje.Media.Gpu;
using Rodaje.Media.Video;
using Rodaje.Render.Gpu;
using Rodaje.Render.Gpu.D3D11;
using Rodaje.Render.Interpolation;
using Rodaje.Render.Shaders;
using Rodaje.Render.Tests.Gpu;
using Rodaje.Render.Tests.Support;
using TerraFX.Interop.DirectX;
using static Rodaje.Render.RenderException;

namespace Rodaje.Render.Tests.Interpolation;

/// <summary>Profundidad de los planos de prueba: NV12 (8 bits), P010 o P016 (16 bits en la textura).</summary>
public enum PlaneDepth
{
    Nv12,
    P010,
    P016,
}

/// <summary>
/// El banco de la interpolación en el motor de <see cref="TestGpu.Engine"/>: con D3D11, un <see cref="GpuDevice"/> WARP,
/// su <see cref="PlaneViewCache"/> y el <see cref="FrameInterpolator"/> de siempre; con Vulkan, el
/// <see cref="VulkanTestGpu.Gpu"/> compartido (no se libera) y el <see cref="SurfaceInterpolator"/>. Las entradas y las
/// lecturas llevan los mismos códigos en los dos motores: NV12 en R8/Rg8, P010 y P016 en R16/Rg16.
/// </summary>
internal sealed class InterpolationRig : IDisposable
{
    private readonly GpuDevice? _device;
    private readonly PlaneViewCache? _views;
    private readonly GpuSurfaceCache? _cache;
    private readonly FrameInterpolator? _frames;
    private readonly SurfaceInterpolator? _surfaces;
    private readonly IGpu _gpu;
    private readonly List<IDecoderSurface> _uploads = [];

    private InterpolationRig(GpuDevice? device, PlaneViewCache? views, GpuSurfaceCache? cache,
        FrameInterpolator? frames, SurfaceInterpolator? surfaces, IGpu gpu, RenderTargetPool pool)
    {
        _device = device;
        _views = views;
        _cache = cache;
        _frames = frames;
        _surfaces = surfaces;
        _gpu = gpu;
        Pool = pool;
    }

    public RenderTargetPool Pool { get; }

    public int TextureCount =>
        OperatingSystem.IsWindowsVersionAtLeast(7) && _frames is not null
            ? _frames.TextureCount
            : _surfaces?.TextureCount ?? 0;

    public long FlowFrames =>
        OperatingSystem.IsWindowsVersionAtLeast(7) && _frames is not null
            ? _frames.FlowFrames
            : _surfaces?.FlowFrames ?? 0;

    public static InterpolationRig Create(long poolCapBytes = 1024 * 1024, ShaderCache? shaders = null)
    {
        shaders ??= new ShaderCache(null);
        if (TestGpu.IsVulkan || !OperatingSystem.IsWindowsVersionAtLeast(7))
        {
            Rodaje.Render.Gpu.Vulkan.VulkanGpu gpu = VulkanTestGpu.Gpu;
            GpuSurfaceCache cache = gpu.CreateSurfaceCache();
            var pool = new RenderTargetPool(gpu, poolCapBytes);
            return new InterpolationRig(null, null, cache, null, new SurfaceInterpolator(gpu, cache, pool, shaders), gpu, pool);
        }

        return CreateD3D11(poolCapBytes, shaders);
    }

    [SupportedOSPlatform("windows7.0")]
    private static unsafe InterpolationRig CreateD3D11(long poolCapBytes, ShaderCache shaders)
    {
        GpuDevice device = GpuDevice.CreateWarp();
        try
        {
            var views = new PlaneViewCache((ID3D11Device*)device.NativeDevice);
            var engine = D3D11Gpu.For(device);
            var pool = new RenderTargetPool(engine, poolCapBytes);
            return new InterpolationRig(device, views, null, new FrameInterpolator(device, views, pool, shaders), null, engine, pool);
        }
        catch
        {
            device.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Una superficie de <paramref name="codedWidth"/>×<paramref name="codedHeight"/> con <paramref name="codes"/> tal cual
    /// (luma, luego la croma entrelazada), visible de <paramref name="visibleWidth"/>×<paramref name="visibleHeight"/>.
    /// </summary>
    public IDecoderSurface Upload(ushort[] codes, int codedWidth, int codedHeight, int visibleWidth, int visibleHeight,
        PlaneDepth depth, VideoColor color)
    {
        int samples = codedWidth * codedHeight;
        byte[] luma = new byte[samples * (depth == PlaneDepth.Nv12 ? 1 : 2)];
        byte[] chroma = new byte[samples / 2 * (depth == PlaneDepth.Nv12 ? 1 : 2)];
        if (depth == PlaneDepth.Nv12)
        {
            for (int i = 0; i < samples; i++) luma[i] = (byte)codes[i];
            for (int i = 0; i < samples / 2; i++) chroma[i] = (byte)codes[samples + i];
        }
        else
        {
            System.Buffer.BlockCopy(codes, 0, luma, 0, luma.Length);
            System.Buffer.BlockCopy(codes, samples * 2, chroma, 0, chroma.Length);
        }

        IDecoderSurface surface = TestGpu.IsVulkan || !OperatingSystem.IsWindowsVersionAtLeast(7)
            ? UploadVulkan(luma, chroma, codedWidth, codedHeight, visibleWidth, visibleHeight, depth, color)
            : UploadD3D11(luma, chroma, codedWidth, codedHeight, visibleWidth, visibleHeight, depth, color);
        _uploads.Add(surface);
        return surface;
    }

    /// <summary>Una superficie RGBA de una textura, sin croma: la rechazan los dos interpoladores.</summary>
    public IDecoderSurface RgbaSurface(int width, int height)
    {
        IDecoderSurface surface = TestGpu.IsVulkan || !OperatingSystem.IsWindowsVersionAtLeast(7)
            ? new GpuTextureSurface(_gpu.CreateTexture(new GpuTextureDesc(width, height, GpuFormat.Rgba8Unorm, GpuTextureUsage.Sampled)),
                null, FramePixelFormat.Bgra8, VideoColor.Bt709Limited)
            : RgbaD3D11(width, height);
        _uploads.Add(surface);
        return surface;
    }

    [SupportedOSPlatform("windows7.0")]
    private unsafe D3D11TexturePayload RgbaD3D11(int width, int height)
    {
        using RenderTarget target = RenderTarget.Create(D3D11Gpu.For(_device!), width, height, GpuFormat.Bgra8Unorm);
        return D3D11TexturePayload.FromTexture(_device!, (nint)target.Texture, 0, width, height, VideoColor.Bt709Limited);
    }

    /// <summary>Mismo recurso de salida (otro slot vale como "distinto").</summary>
    public static bool SameTexture(IDecoderSurface a, IDecoderSurface b) =>
        a is GpuTextureSurface sa && b is GpuTextureSurface sb
            ? ReferenceEquals(sa.Luma, sb.Luma)
            : OperatingSystem.IsWindowsVersionAtLeast(7) && SameTextureD3D11(a, b);

    [SupportedOSPlatform("windows7.0")]
    private static bool SameTextureD3D11(IDecoderSurface a, IDecoderSurface b) =>
        ((D3D11TexturePayload)a).Texture == ((D3D11TexturePayload)b).Texture;

    private GpuTextureSurface UploadVulkan(byte[] luma, byte[] chroma, int codedWidth, int codedHeight,
        int visibleWidth, int visibleHeight, PlaneDepth depth, VideoColor color)
    {
        bool eight = depth == PlaneDepth.Nv12;
        GpuTexture lumaTexture = _gpu.CreateTexture(new GpuTextureDesc(codedWidth, codedHeight,
            eight ? GpuFormat.R8Unorm : GpuFormat.R16Unorm, GpuTextureUsage.Sampled), luma);
        try
        {
            GpuTexture chromaTexture = _gpu.CreateTexture(new GpuTextureDesc(codedWidth / 2, codedHeight / 2,
                eight ? GpuFormat.Rg8Unorm : GpuFormat.Rg16Unorm, GpuTextureUsage.Sampled), chroma);
            return new GpuTextureSurface(lumaTexture, chromaTexture,
                depth switch { PlaneDepth.Nv12 => FramePixelFormat.Nv12, PlaneDepth.P010 => FramePixelFormat.P010, _ => FramePixelFormat.P016 },
                color, visibleWidth, visibleHeight);
        }
        catch
        {
            lumaTexture.Dispose();
            throw;
        }
    }

    [SupportedOSPlatform("windows7.0")]
    private unsafe D3D11TexturePayload UploadD3D11(byte[] luma, byte[] chroma, int codedWidth, int codedHeight,
        int visibleWidth, int visibleHeight, PlaneDepth depth, VideoColor color)
    {
        byte[] data = new byte[luma.Length + chroma.Length];
        luma.CopyTo(data, 0);
        chroma.CopyTo(data, luma.Length);
        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width = (uint)codedWidth, Height = (uint)codedHeight, MipLevels = 1, ArraySize = 1,
            Format = depth switch { PlaneDepth.Nv12 => DXGI_FORMAT.DXGI_FORMAT_NV12, PlaneDepth.P010 => DXGI_FORMAT.DXGI_FORMAT_P010, _ => DXGI_FORMAT.DXGI_FORMAT_P016 },
            SampleDesc = new DXGI_SAMPLE_DESC(1, 0), Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
            BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE,
        };
        ID3D11Texture2D* texture;
        fixed (byte* source = data)
        {
            var initial = new D3D11_SUBRESOURCE_DATA { pSysMem = source, SysMemPitch = (uint)(codedWidth * (depth == PlaneDepth.Nv12 ? 1 : 2)) };
            Check(((ID3D11Device*)_device!.NativeDevice)->CreateTexture2D(&desc, &initial, &texture), "CreateTexture2D(prueba de interpolación)");
        }

        try
        {
            return D3D11TexturePayload.FromTexture(_device!, (nint)texture, 0, visibleWidth, visibleHeight, color);
        }
        finally
        {
            texture->Release();
        }
    }

    public IDecoderSurface? Blend(IDecoderSurface a, IDecoderSurface b, float fraction) =>
        _frames is null || !OperatingSystem.IsWindowsVersionAtLeast(7)
            ? _surfaces!.Blend(a, b, fraction)
            : _frames.Blend((D3D11TexturePayload)a, (D3D11TexturePayload)b, fraction);

    public IDecoderSurface? Flow(IDecoderSurface a, IDecoderSurface b, float fraction) =>
        _frames is null || !OperatingSystem.IsWindowsVersionAtLeast(7)
            ? _surfaces!.Flow(a, b, fraction)
            : _frames.Flow((D3D11TexturePayload)a, (D3D11TexturePayload)b, fraction);

    /// <summary>Los códigos visibles: filas de luma y luego de croma entrelazada, <c>width</c> códigos por fila.</summary>
    public ushort[] ReadPlanes(IDecoderSurface result)
    {
        if (_frames is not null && OperatingSystem.IsWindowsVersionAtLeast(7))
        {
            return ReadPlanesD3D11((D3D11TexturePayload)result);
        }

        var surface = (GpuTextureSurface)result;
        int width = surface.Width;
        int height = surface.Height;
        var codes = new ushort[width * height * 3 / 2];
        ReadPlane(surface.Luma, width, height, codes.AsSpan(0, width * height), width, false);
        ReadPlane(surface.Chroma, width / 2, height / 2, codes.AsSpan(width * height), width, true);
        return codes;
    }

    private void ReadPlane(GpuTexture texture, int width, int height, Span<ushort> codes, int codesPerRow, bool pairs)
    {
        using GpuReadback readback = _gpu.CreateReadback(width, height, texture.Format);
        using (IGpuFrame frame = _gpu.BeginFrame())
        {
            frame.Copy(texture, new GpuBox(0, 0, width, height), readback);
        }

        using GpuMappedRows rows = readback.Map();
        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<byte> row = rows.Row(y);
            if (texture.Format is GpuFormat.R8Unorm or GpuFormat.Rg8Unorm)
            {
                for (int x = 0; x < width * (pairs ? 2 : 1); x++) codes[(y * codesPerRow) + x] = row[x];
            }
            else
            {
                ReadOnlySpan<ushort> words = MemoryMarshal.Cast<byte, ushort>(row);
                for (int x = 0; x < width * (pairs ? 2 : 1); x++) codes[(y * codesPerRow) + x] = words[x];
            }
        }
    }

    /// <summary>(corte, distancia del histograma, residuo) del último flujo; <c>null</c> si no hubo.</summary>
    public unsafe (bool Cut, float Histogram, float Residual)? ReadDecision()
    {
        if (_frames is { } frames && OperatingSystem.IsWindowsVersionAtLeast(7))
        {
            return ReadDecisionD3D11(frames.FlowDecisionBuffer);
        }

        GpuBuffer? decision = _surfaces?.FlowDecision;
        if (decision is null)
        {
            return null;
        }

        using GpuReadback readback = _gpu.CreateReadback(16);
        using (IGpuFrame frame = _gpu.BeginFrame())
        {
            frame.Copy(decision, 0, 16, readback);
        }

        using GpuMappedRows rows = readback.Map();
        ReadOnlySpan<float> values = MemoryMarshal.Cast<byte, float>(rows.Row(0));
        return (values[0] != 0, values[1], values[2]);
    }

    [SupportedOSPlatform("windows7.0")]
    private unsafe (bool Cut, float Histogram, float Residual)? ReadDecisionD3D11(nint buffer)
    {
        var desc = new D3D11_BUFFER_DESC { ByteWidth = 16, Usage = D3D11_USAGE.D3D11_USAGE_STAGING, CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ };
        ID3D11Buffer* staging;
        Check(((ID3D11Device*)_device!.NativeDevice)->CreateBuffer(&desc, null, &staging), "CreateBuffer(lectura del corte)");
        var context = (ID3D11DeviceContext*)_device.NativeContext;
        try
        {
            context->CopyResource((ID3D11Resource*)staging, (ID3D11Resource*)buffer);
            D3D11_MAPPED_SUBRESOURCE mapped;
            Check(context->Map((ID3D11Resource*)staging, 0, D3D11_MAP.D3D11_MAP_READ, 0, &mapped), "Map(lectura del corte)");
            try { float* values = (float*)mapped.pData; return (values[0] != 0, values[1], values[2]); }
            finally { context->Unmap((ID3D11Resource*)staging, 0); }
        }
        finally
        {
            staging->Release();
        }
    }

    [SupportedOSPlatform("windows7.0")]
    private unsafe ushort[] ReadPlanesD3D11(D3D11TexturePayload payload)
    {
        var device = (ID3D11Device*)_device!.NativeDevice;
        var context = (ID3D11DeviceContext*)_device.NativeContext;
        var texture = (ID3D11Texture2D*)payload.Texture;
        D3D11_TEXTURE2D_DESC desc;
        texture->GetDesc(&desc);
        desc.ArraySize = 1;
        desc.BindFlags = 0;
        desc.Usage = D3D11_USAGE.D3D11_USAGE_STAGING;
        desc.CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ;
        ID3D11Texture2D* staging;
        Check(device->CreateTexture2D(&desc, null, &staging), "CreateTexture2D(lectura de planos)");
        try
        {
            context->CopySubresourceRegion((ID3D11Resource*)staging, 0, 0, 0, 0, (ID3D11Resource*)texture, (uint)payload.ArraySlice, null);
            D3D11_MAPPED_SUBRESOURCE mapped;
            Check(context->Map((ID3D11Resource*)staging, 0, D3D11_MAP.D3D11_MAP_READ, 0, &mapped), "Map(lectura de planos)");
            try
            {
                int width = payload.Width;
                int height = payload.Height;
                var codes = new ushort[width * height * 3 / 2];
                bool eight = payload.DxgiFormat == (int)DXGI_FORMAT.DXGI_FORMAT_NV12;
                for (int y = 0; y < height * 3 / 2; y++)
                {
                    int row = y < height ? y : payload.CodedHeight + y - height;
                    byte* data = (byte*)mapped.pData + ((nuint)row * mapped.RowPitch);
                    for (int x = 0; x < width; x++) codes[(y * width) + x] = eight ? data[x] : ((ushort*)data)[x];
                }

                return codes;
            }
            finally
            {
                context->Unmap((ID3D11Resource*)staging, 0);
            }
        }
        finally
        {
            staging->Release();
        }
    }

    public void EndFrame()
    {
        if (OperatingSystem.IsWindowsVersionAtLeast(7))
        {
            _frames?.EndFrame();
        }

        _surfaces?.EndFrame();
    }

    public void Trim()
    {
        if (OperatingSystem.IsWindowsVersionAtLeast(7))
        {
            _frames?.Trim();
        }

        _surfaces?.Trim();
    }

    public void Dispose()
    {
        if (OperatingSystem.IsWindowsVersionAtLeast(7))
        {
            _frames?.Dispose();
            _views?.Dispose();
            _device?.Dispose();
        }

        _surfaces?.Dispose();
        _cache?.Dispose();
        foreach (IDecoderSurface upload in _uploads)
        {
            upload.Release();
        }

        _uploads.Clear();
        Pool.Dispose();
    }
}
