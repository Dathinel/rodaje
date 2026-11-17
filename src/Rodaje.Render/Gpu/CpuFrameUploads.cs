using System.Runtime.InteropServices;
using Rodaje.Media.Video;

namespace Rodaje.Render.Gpu;

/// <summary>
/// Cuadros decodificados en la CPU (sin D3D11VA: Linux y macOS de momento) subidos a texturas del motor como NV12 o P010, para que el
/// compositor los dibuje con el mismo camino de muestreo. Cada cuadro se sube una vez y se suelta cuando lleva un cuadro compuesto sin usarse.
/// </summary>
internal sealed class CpuFrameUploads(IGpu gpu) : IDisposable
{
    private readonly Dictionary<CpuFramePayload, (GpuTextureSurface Surface, long Frame)> _surfaces = [];
    private long _frame;

    public int Count => _surfaces.Count;

    /// <summary>La superficie de <paramref name="payload"/>; <c>null</c> si su formato no se sabe subir (se salta, como hasta ahora).</summary>
    public GpuTextureSurface? Get(CpuFramePayload payload)
    {
        if (payload.IsDisposed)
        {
            return null;
        }

        if (_surfaces.TryGetValue(payload, out (GpuTextureSurface Surface, long Frame) entry))
        {
            _surfaces[payload] = entry with { Frame = _frame };
            return entry.Surface;
        }

        if (Upload(payload) is not { } surface)
        {
            return null;
        }

        _surfaces.Add(payload, (surface, _frame));
        return surface;
    }

    public void EndFrame()
    {
        _frame++;
        List<CpuFramePayload>? stale = null;
        foreach ((CpuFramePayload payload, (GpuTextureSurface _, long frame)) in _surfaces)
        {
            if (frame < _frame - 1 || payload.IsDisposed)
            {
                (stale ??= []).Add(payload);
            }
        }

        foreach (CpuFramePayload payload in stale ?? [])
        {
            _surfaces[payload].Surface.Release();
            _surfaces.Remove(payload);
        }
    }

    public void Dispose()
    {
        foreach ((GpuTextureSurface surface, long _) in _surfaces.Values)
        {
            surface.Release();
        }

        _surfaces.Clear();
    }

    private GpuTextureSurface? Upload(CpuFramePayload payload)
    {
        int width = payload.Width;
        int height = payload.Height;
        int chromaWidth = (width + 1) / 2;
        int chromaHeight = (height + 1) / 2;
        (bool deep, bool planar) = payload.PixelFormat switch
        {
            FramePixelFormat.Nv12 => (false, false),
            FramePixelFormat.P010 => (true, false),
            FramePixelFormat.Yuv420P => (false, true),
            FramePixelFormat.Yuv420P10 => (true, true),
            _ => (false, false),
        };
        if (payload.PixelFormat is not (FramePixelFormat.Nv12 or FramePixelFormat.P010 or FramePixelFormat.Yuv420P or FramePixelFormat.Yuv420P10))
        {
            return null;
        }

        int sample = deep ? 2 : 1;
        // yuv420p10 guarda el código en los 10 bits bajos; P010, en los altos, que es lo que leen las vistas R16_UNORM.
        int shift = deep && planar ? 6 : 0;
        byte[] luma = new byte[width * height * sample];
        ReadOnlySpan<byte> source = payload.GetPlane(0, out int stride);
        for (int y = 0; y < height; y++)
        {
            Widen(source.Slice(y * stride, width * sample), luma.AsSpan(y * width * sample, width * sample), deep, shift);
        }

        byte[] chroma = new byte[chromaWidth * chromaHeight * 2 * sample];
        if (planar)
        {
            ReadOnlySpan<byte> u = payload.GetPlane(1, out int uStride);
            ReadOnlySpan<byte> v = payload.GetPlane(2, out int vStride);
            for (int y = 0; y < chromaHeight; y++)
            {
                Interleave(u.Slice(y * uStride, chromaWidth * sample), v.Slice(y * vStride, chromaWidth * sample),
                    chroma.AsSpan(y * chromaWidth * 2 * sample, chromaWidth * 2 * sample), deep, shift);
            }
        }
        else
        {
            ReadOnlySpan<byte> uv = payload.GetPlane(1, out int uvStride);
            for (int y = 0; y < chromaHeight; y++)
            {
                uv.Slice(y * uvStride, chromaWidth * 2 * sample).CopyTo(chroma.AsSpan(y * chromaWidth * 2 * sample));
            }
        }

        GpuTexture lumaTexture = gpu.CreateTexture(new GpuTextureDesc(width, height, deep ? GpuFormat.R16Unorm : GpuFormat.R8Unorm,
            GpuTextureUsage.Sampled), luma);
        try
        {
            GpuTexture chromaTexture = gpu.CreateTexture(new GpuTextureDesc(chromaWidth, chromaHeight, deep ? GpuFormat.Rg16Unorm : GpuFormat.Rg8Unorm,
                GpuTextureUsage.Sampled), chroma);
            return new GpuTextureSurface(lumaTexture, chromaTexture, deep ? FramePixelFormat.P010 : FramePixelFormat.Nv12, payload.Color);
        }
        catch
        {
            lumaTexture.Dispose();
            throw;
        }
    }

    private static void Widen(ReadOnlySpan<byte> source, Span<byte> target, bool deep, int shift)
    {
        if (!deep || shift == 0)
        {
            source.CopyTo(target);
            return;
        }

        ReadOnlySpan<ushort> from = MemoryMarshal.Cast<byte, ushort>(source);
        Span<ushort> to = MemoryMarshal.Cast<byte, ushort>(target);
        for (int i = 0; i < from.Length; i++)
        {
            to[i] = (ushort)(from[i] << shift);
        }
    }

    private static void Interleave(ReadOnlySpan<byte> u, ReadOnlySpan<byte> v, Span<byte> target, bool deep, int shift)
    {
        if (!deep)
        {
            for (int i = 0; i < u.Length; i++)
            {
                target[2 * i] = u[i];
                target[(2 * i) + 1] = v[i];
            }

            return;
        }

        ReadOnlySpan<ushort> u16 = MemoryMarshal.Cast<byte, ushort>(u);
        ReadOnlySpan<ushort> v16 = MemoryMarshal.Cast<byte, ushort>(v);
        Span<ushort> to = MemoryMarshal.Cast<byte, ushort>(target);
        for (int i = 0; i < u16.Length; i++)
        {
            to[2 * i] = (ushort)(u16[i] << shift);
            to[(2 * i) + 1] = (ushort)(v16[i] << shift);
        }
    }
}
