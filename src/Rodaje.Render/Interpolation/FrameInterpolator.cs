using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Rodaje.Media.Gpu;
using Rodaje.Render.Gpu;
using Rodaje.Render.Gpu.D3D11;
using Rodaje.Render.Shaders;
using TerraFX.Interop.DirectX;
using static Rodaje.Render.RenderException;

namespace Rodaje.Render.Interpolation;

/// <summary>Mezcla los códigos de los planos del origen; las salidas se devuelven al terminar el cuadro.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows7.0")]
internal sealed unsafe class FrameInterpolator(GpuDevice gpu, PlaneViewCache views, RenderTargetPool budget, ShaderCache shaders) : IDisposable
{
    private readonly D3D11Gpu _engine = D3D11Gpu.For(gpu);
    private readonly List<Slot> _slots = [];
    private readonly List<D3D11TexturePayload> _payloads = [];
    private GpuPipeline? _lumaPipeline;
    private GpuPipeline? _chromaPipeline;
    private GpuBuffer? _constants;
    private long _frame;
    private FlowInterpolator? _flow;

    public int TextureCount => _slots.Count + (_flow?.TextureCount ?? 0);
    internal long FlowFrames => _flow?.Frames ?? 0;
    internal nint FlowDecisionBuffer => _flow?.Decision is D3D11Buffer d ? (nint)d.Native : 0;

    public D3D11TexturePayload? Blend(D3D11TexturePayload a, D3D11TexturePayload b, float fraction) => Interpolate(a, b, fraction, false);

    public D3D11TexturePayload? Flow(D3D11TexturePayload a, D3D11TexturePayload b, float fraction)
    {
        if (a.Device != gpu || b.Device != gpu) return null;
        try
        {
            return Interpolate(a, b, fraction, true);
        }
        catch (Exception error) when (error is RenderException or OutOfMemoryException or NotSupportedException)
        {
            _flow?.Trim();
            try { return Blend(a, b, fraction); }
            catch (Exception fallback) when (fallback is RenderException or OutOfMemoryException or NotSupportedException) { return null; }
        }
    }

    private D3D11TexturePayload? Interpolate(D3D11TexturePayload a, D3D11TexturePayload b, float fraction, bool opticalFlow)
    {
        if (a.IsReleased || b.IsReleased || a.DxgiFormat != b.DxgiFormat || a.PixelFormat != b.PixelFormat
            || a.Width != b.Width || a.Height != b.Height
            || a.DxgiFormat is not ((int)DXGI_FORMAT.DXGI_FORMAT_NV12 or (int)DXGI_FORMAT.DXGI_FORMAT_P010 or (int)DXGI_FORMAT.DXGI_FORMAT_P016)
            || !float.IsFinite(fraction))
        {
            return null;
        }

        if (a.Device != gpu || b.Device != gpu)
        {
            throw new ArgumentException("Los cuadros de interpolación son de otro dispositivo.");
        }

        int width = a.CodedWidth;
        int height = a.CodedHeight;
        DXGI_FORMAT format = (DXGI_FORMAT)a.DxgiFormat;
        Slot? slot = _slots.Find(s => !s.InUse && s.Width == width && s.Height == height && s.Format == format);
        if (slot is null)
        {
            long bytes = (long)width * height * 3 / 2 * (format == DXGI_FORMAT.DXGI_FORMAT_NV12 ? 1 : 2);
            while (!budget.Reserve(bytes))
            {
                if (opticalFlow && _flow is { TextureCount: > 0 })
                {
                    _flow.Trim();
                    continue;
                }

                int free = _slots.FindIndex(s => !s.InUse);
                if (free < 0)
                {
                    return null;
                }

                Remove(free);
            }

            try
            {
                slot = CreateSlot(width, height, format, bytes);
                _slots.Add(slot);
            }
            catch (RenderException)
            {
                // Un controlador sin vistas de escritura sobre planos de video: la capa sale con el cuadro A, no se cae el cuadro.
                budget.ReleaseReservation(bytes);
                return null;
            }
            catch
            {
                budget.ReleaseReservation(bytes);
                throw;
            }
        }

        EnsureShaders();
        slot.InUse = true;
        PlaneViews first = views.Get(a, out bool ownFirst);
        try
        {
            PlaneViews second = views.Get(b, out bool ownSecond);
            try
            {
                bool flowed = opticalFlow && (_flow ??= new FlowInterpolator(_engine, budget, shaders))
                    .TryDispatch(first.LumaTexture, first.ChromaTexture, second.LumaTexture, second.ChromaTexture,
                        slot.Luma, slot.Chroma, a.Width, a.Height,
                        format == DXGI_FORMAT.DXGI_FORMAT_NV12, Math.Clamp(fraction, 0, 1));
                if (!flowed)
                {
                    Dispatch(_lumaPipeline!, first.LumaTexture, second.LumaTexture, slot.Luma, width, height, fraction);
                    Dispatch(_chromaPipeline!, first.ChromaTexture, second.ChromaTexture, slot.Chroma, width / 2, height / 2, fraction);
                }
            }
            finally
            {
                if (ownSecond) second.Release();
            }
        }
        finally
        {
            if (ownFirst) first.Release();
        }

        D3D11TexturePayload payload = D3D11TexturePayload.FromTexture(gpu, (nint)((D3D11Texture)slot.Texture).Texture2D, 0, a.Width, a.Height, a.Color, a.PixelFormat);
        _payloads.Add(payload);
        return payload;
    }

    public void EndFrame()
    {
        foreach (D3D11TexturePayload payload in _payloads) payload.Dispose();
        _payloads.Clear();
        foreach (Slot slot in _slots)
        {
            if (slot.InUse) slot.LastUsed = _frame;
            slot.InUse = false;
        }

        _frame++;
        for (int i = _slots.Count - 1; i >= 0; i--)
        {
            if (_frame - _slots[i].LastUsed > 240) Remove(i);
        }
    }

    public void Trim()
    {
        _flow?.Trim();
        for (int i = _slots.Count - 1; i >= 0; i--)
        {
            if (!_slots[i].InUse) Remove(i);
        }
    }

    public void Dispose()
    {
        EndFrame();
        Trim();
        _flow?.Dispose();
        _lumaPipeline?.Desc.Compute?.Dispose();
        _lumaPipeline?.Dispose();
        _chromaPipeline?.Desc.Compute?.Dispose();
        _chromaPipeline?.Dispose();
        _constants?.Dispose();
    }

    private void Remove(int index)
    {
        Slot slot = _slots[index];
        _slots.RemoveAt(index);
        budget.ReleaseReservation(slot.Bytes);
        slot.Dispose();
    }

    private Slot CreateSlot(int width, int height, DXGI_FORMAT format, long bytes)
    {
        var slot = new Slot(width, height, format, bytes);
        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1, Format = format,
            SampleDesc = new DXGI_SAMPLE_DESC(1, 0), Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
            BindFlags = (uint)(D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_FLAG.D3D11_BIND_UNORDERED_ACCESS),
        };
        var device = (ID3D11Device*)gpu.NativeDevice;
        ID3D11Texture2D* texture;
        Check(device->CreateTexture2D(&desc, null, &texture), "CreateTexture2D(interpolación)");
        try
        {
            slot.Texture = D3D11Texture.Wrap(_engine, texture,
                new GpuTextureDesc(width, height, GpuFormat.Unknown, GpuTextureUsage.Sampled | GpuTextureUsage.Storage), null, null);
            bool eight = format == DXGI_FORMAT.DXGI_FORMAT_NV12;
            slot.Luma = CreatePlaneUav(texture, eight ? DXGI_FORMAT.DXGI_FORMAT_R8_UNORM : DXGI_FORMAT.DXGI_FORMAT_R16_UNORM,
                width, height, eight ? GpuFormat.R8Unorm : GpuFormat.R16Unorm);
            slot.Chroma = CreatePlaneUav(texture, eight ? DXGI_FORMAT.DXGI_FORMAT_R8G8_UNORM : DXGI_FORMAT.DXGI_FORMAT_R16G16_UNORM,
                width / 2, height / 2, eight ? GpuFormat.Rg8Unorm : GpuFormat.Rg16Unorm);
            return slot;
        }
        catch
        {
            slot.Dispose();
            throw;
        }
        finally
        {
            texture->Release();
        }
    }

    private D3D11Texture CreatePlaneUav(ID3D11Texture2D* texture, DXGI_FORMAT format, int width, int height, GpuFormat gpuFormat)
    {
        var desc = new D3D11_UNORDERED_ACCESS_VIEW_DESC { Format = format, ViewDimension = D3D11_UAV_DIMENSION.D3D11_UAV_DIMENSION_TEXTURE2D };
        ID3D11UnorderedAccessView* view;
        Check(((ID3D11Device*)gpu.NativeDevice)->CreateUnorderedAccessView((ID3D11Resource*)texture, &desc, &view), "CreateUnorderedAccessView(interpolación)");
        try
        {
            return D3D11Texture.View(new GpuTextureDesc(width, height, gpuFormat, GpuTextureUsage.Storage), uav: view);
        }
        finally
        {
            view->Release();
        }
    }

    private void EnsureShaders()
    {
        _lumaPipeline ??= _engine.CreatePipeline(new GpuPipelineDesc { Compute = _engine.CreateShader(FlowShaders.Luma, shaders) });
        _chromaPipeline ??= _engine.CreatePipeline(new GpuPipelineDesc { Compute = _engine.CreateShader(FlowShaders.Chroma, shaders) });
        _constants ??= _engine.CreateBuffer(new GpuBufferDesc(sizeof(Constants), GpuBufferKind.Constant));
    }

    private void Dispatch(GpuPipeline pipeline, GpuTexture a, GpuTexture b, GpuTexture output, int width, int height, float fraction)
    {
        using IGpuFrame frame = _engine.BeginFrame();
        var constants = new Constants { Width = (uint)width, Height = (uint)height, Fraction = Math.Clamp(fraction, 0, 1) };
        frame.Write(_constants!, MemoryMarshal.CreateReadOnlySpan(ref constants, 1));
        var bindings = default(GpuBindings);
        bindings.SetConstants(0, _constants);
        bindings.SetResource(0, a);
        bindings.SetResource(1, b);
        bindings.SetStorage(0, output);
        frame.Dispatch(pipeline, bindings, (width + 7) / 8, (height + 7) / 8);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Constants
    {
        public uint Width;
        public uint Height;
        public float Fraction;
        public uint Padding;
    }

    private sealed class Slot(int width, int height, DXGI_FORMAT format, long bytes) : IDisposable
    {
        public readonly int Width = width;
        public readonly int Height = height;
        public readonly DXGI_FORMAT Format = format;
        public readonly long Bytes = bytes;
        public GpuTexture Texture = null!;
        public GpuTexture Luma = null!;
        public GpuTexture Chroma = null!;
        public bool InUse;
        public long LastUsed;

        public void Dispose()
        {
            Chroma?.Dispose();
            Luma?.Dispose();
            Texture?.Dispose();
        }
    }
}
