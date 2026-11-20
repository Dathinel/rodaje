using System.Runtime.InteropServices;
using Rodaje.Media.Gpu;
using Rodaje.Media.Video;
using Rodaje.Render.Gpu;
using Rodaje.Render.Shaders;

namespace Rodaje.Render.Interpolation;

/// <summary>
/// La interpolación de <see cref="FrameInterpolator"/> para cualquier <see cref="IGpu"/>: mezcla y flujo óptico sobre los
/// planos de una <see cref="GpuTextureSurface"/> (NV12/P010/P016 como R8/Rg8 o R16/Rg16). Las salidas se devuelven al
/// terminar el cuadro; un slot cuenta una textura, como la NV12 de D3D11.
/// </summary>
internal sealed unsafe class SurfaceInterpolator(IGpu gpu, GpuSurfaceCache views, RenderTargetPool budget, ShaderCache shaders) : IDisposable
{
    private readonly List<Slot> _slots = [];
    private readonly List<GpuTextureSurface> _outputs = [];
    private GpuPipeline? _lumaPipeline;
    private GpuPipeline? _chromaPipeline;
    private GpuBuffer? _constants;
    private long _frame;
    private FlowInterpolator? _flow;

    public int TextureCount => _slots.Count + (_flow?.TextureCount ?? 0);
    internal long FlowFrames => _flow?.Frames ?? 0;
    internal GpuBuffer? FlowDecision => _flow?.Decision;

    public GpuTextureSurface? Blend(IDecoderSurface a, IDecoderSurface b, float fraction)
    {
        try
        {
            return Interpolate(a, b, fraction, false);
        }
        catch (Exception error) when (error is RenderException or OutOfMemoryException or NotSupportedException)
        {
            // Un dispositivo sin escritura de sombreador en R16/Rg16 deja la capa sin interpolar.
            return null;
        }
    }

    public GpuTextureSurface? Flow(IDecoderSurface a, IDecoderSurface b, float fraction)
    {
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

    private GpuTextureSurface? Interpolate(IDecoderSurface a, IDecoderSurface b, float fraction, bool opticalFlow)
    {
        if (a.IsReleased || b.IsReleased || !float.IsFinite(fraction)
            || a.PixelFormat != b.PixelFormat
            || a.PixelFormat is not (FramePixelFormat.Nv12 or FramePixelFormat.P010 or FramePixelFormat.P016)
            || a.Width != b.Width || a.Height != b.Height)
        {
            return null;
        }

        using IGpuFrame frame = gpu.BeginFrame();
        GpuDecoderPlanes planesA = frame.Acquire(views, a);
        GpuDecoderPlanes planesB = frame.Acquire(views, b);
        GpuTexture luma = planesA.Luma;
        GpuTexture chroma = planesA.Chroma;
        if (ReferenceEquals(luma, chroma)
            || (luma.Format, chroma.Format) is not ((GpuFormat.R8Unorm, GpuFormat.Rg8Unorm) or (GpuFormat.R16Unorm, GpuFormat.Rg16Unorm))
            || planesB.Luma.Format != luma.Format || planesB.Chroma.Format != chroma.Format)
        {
            return null;
        }

        Slot? slot = _slots.Find(s => !s.InUse && s.LumaWidth == luma.Width && s.LumaHeight == luma.Height
            && s.LumaFormat == luma.Format && s.ChromaWidth == chroma.Width && s.ChromaHeight == chroma.Height);
        if (slot is null)
        {
            int bytesPerSample = luma.Format == GpuFormat.R8Unorm ? 1 : 2;
            long bytes = ((long)luma.Width * luma.Height + (long)chroma.Width * chroma.Height * 2) * bytesPerSample;
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
                slot = CreateSlot(luma.Width, luma.Height, luma.Format, chroma.Width, chroma.Height, bytes);
                _slots.Add(slot);
            }
            catch (RenderException)
            {
                // Un dispositivo sin escritura de sombreador en el formato de los planos: la capa sale con el cuadro A.
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
        bool flowed = opticalFlow && (_flow ??= new FlowInterpolator(gpu, budget, shaders))
            .TryDispatch(luma, chroma, planesB.Luma, planesB.Chroma, slot.Luma, slot.Chroma, a.Width, a.Height,
                luma.Format == GpuFormat.R8Unorm, Math.Clamp(fraction, 0, 1));
        if (!flowed)
        {
            // Con relleno distinto, B puede ser menor: en Vulkan leer fuera de la imagen no está definido (D3D11 da 0).
            Dispatch(frame, _lumaPipeline!, luma, planesB.Luma, slot.Luma,
                Math.Min(luma.Width, planesB.Luma.Width), Math.Min(luma.Height, planesB.Luma.Height), fraction);
            Dispatch(frame, _chromaPipeline!, chroma, planesB.Chroma, slot.Chroma,
                Math.Min(chroma.Width, planesB.Chroma.Width), Math.Min(chroma.Height, planesB.Chroma.Height), fraction);
        }

        var output = new GpuTextureSurface(slot.Luma, slot.Chroma, a.PixelFormat, a.Color, a.Width, a.Height, ownsTextures: false);
        _outputs.Add(output);
        return output;
    }

    public void EndFrame()
    {
        foreach (GpuTextureSurface output in _outputs) output.Release();
        _outputs.Clear();
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

    private Slot CreateSlot(int lumaWidth, int lumaHeight, GpuFormat lumaFormat, int chromaWidth, int chromaHeight, long bytes)
    {
        var slot = new Slot(lumaWidth, lumaHeight, lumaFormat, chromaWidth, chromaHeight, bytes);
        try
        {
            slot.Luma = gpu.CreateTexture(new GpuTextureDesc(lumaWidth, lumaHeight, lumaFormat,
                GpuTextureUsage.Storage | GpuTextureUsage.Sampled));
            slot.Chroma = gpu.CreateTexture(new GpuTextureDesc(chromaWidth, chromaHeight,
                lumaFormat == GpuFormat.R8Unorm ? GpuFormat.Rg8Unorm : GpuFormat.Rg16Unorm,
                GpuTextureUsage.Storage | GpuTextureUsage.Sampled));
            return slot;
        }
        catch
        {
            slot.Dispose();
            throw;
        }
    }

    private void EnsureShaders()
    {
        _lumaPipeline ??= gpu.CreatePipeline(new GpuPipelineDesc { Compute = gpu.CreateShader(FlowShaders.Luma, shaders) });
        _chromaPipeline ??= gpu.CreatePipeline(new GpuPipelineDesc { Compute = gpu.CreateShader(FlowShaders.Chroma, shaders) });
        _constants ??= gpu.CreateBuffer(new GpuBufferDesc(sizeof(Constants), GpuBufferKind.Constant));
    }

    private void Dispatch(IGpuFrame frame, GpuPipeline pipeline, GpuTexture a, GpuTexture b, GpuTexture output,
        int width, int height, float fraction)
    {
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

    private sealed class Slot(int lumaWidth, int lumaHeight, GpuFormat lumaFormat, int chromaWidth, int chromaHeight, long bytes) : IDisposable
    {
        public readonly int LumaWidth = lumaWidth;
        public readonly int LumaHeight = lumaHeight;
        public readonly GpuFormat LumaFormat = lumaFormat;
        public readonly int ChromaWidth = chromaWidth;
        public readonly int ChromaHeight = chromaHeight;
        public readonly long Bytes = bytes;
        public GpuTexture Luma = null!;
        public GpuTexture Chroma = null!;
        public bool InUse;
        public long LastUsed;

        public void Dispose()
        {
            Chroma?.Dispose();
            Luma?.Dispose();
        }
    }
}
