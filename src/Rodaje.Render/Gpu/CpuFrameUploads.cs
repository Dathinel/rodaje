using System.Runtime.InteropServices;
using Rodaje.Media.Video;

namespace Rodaje.Render.Gpu;

/// <summary>
/// Cuadros decodificados en la CPU (sin D3D11VA: Linux y macOS de momento) subidos a texturas del motor (planos de 16 bits como NV12, P010
/// o P016, o BGRA/RGBA16 premultiplicado), para que el compositor los dibuje con el mismo camino de muestreo. Los demás formatos (4:2:2,
/// 4:4:4, más de 10 bits, RGB, alfa) pasan antes por <see cref="CpuFramePayload.ToUploadable"/>, como hace <c>FrameUploader</c> en Windows.
/// Cada cuadro se sube una vez y se suelta cuando lleva un cuadro compuesto sin usarse.
/// </summary>
internal sealed class CpuFrameUploads(IGpu gpu) : IDisposable
{
    private readonly Dictionary<CpuFramePayload, (GpuTextureSurface Surface, long Frame)> _surfaces = [];
    private long _frame;

    public int Count => _surfaces.Count;

    /// <summary>La superficie de <paramref name="payload"/>; <c>null</c> si ya se soltó.</summary>
    /// <exception cref="Rodaje.Media.MediaException">El formato no se sabe convertir: la exportación falla en vez de salir sin la capa.</exception>
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

        GpuTextureSurface surface = Upload(payload);
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

    private GpuTextureSurface Upload(CpuFramePayload payload)
    {
        if (payload.PixelFormat is FramePixelFormat.Other)
        {
            using CpuFramePayload converted = payload.ToUploadable();
            return Upload(converted);
        }

        if (payload.PixelFormat is FramePixelFormat.Bgra8 or FramePixelFormat.Rgba16)
        {
            return UploadRgba(payload);
        }

        int width = payload.Width;
        int height = payload.Height;
        int chromaWidth = (width + 1) / 2;
        int chromaHeight = (height + 1) / 2;
        (bool deep, bool planar) = payload.PixelFormat switch
        {
            FramePixelFormat.Nv12 => (false, false),
            FramePixelFormat.P010 or FramePixelFormat.P016 => (true, false),
            FramePixelFormat.Yuv420P => (false, true),
            FramePixelFormat.Yuv420P10 => (true, true),
            _ => throw new NotSupportedException($"Formato de CPU {payload.PixelFormat} ({payload.FfmpegPixelFormat}) sin subida."),
        };

        // Siempre a 16 bits: lavapipe filtra las texturas UNORM8 con 8 bits de precisión y una capa NV12 se alejaba hasta 6 códigos de 10
        // bits de la de D3D11. Los 8 bits van ×257 (v/255 exacto en UNORM16) y la superficie sigue siendo NV12: la normalización es la de
        // 8 bits también en rango completo (255 es 1, no 1020/1023). yuv420p10 guarda el código en los 10 bits bajos; P010, en los altos,
        // que es lo que leen las vistas R16_UNORM; P016 usa la palabra entera.
        int sourceSample = deep ? 2 : 1;
        int shift = deep && planar ? 6 : 0;
        const int sample = 2;
        byte[] luma = new byte[width * height * sample];
        ReadOnlySpan<byte> source = payload.GetPlane(0, out int stride);
        for (int y = 0; y < height; y++)
        {
            Widen(source.Slice(y * stride, width * sourceSample), luma.AsSpan(y * width * sample, width * sample), deep, shift);
        }

        byte[] chroma = new byte[chromaWidth * chromaHeight * 2 * sample];
        if (planar)
        {
            ReadOnlySpan<byte> u = payload.GetPlane(1, out int uStride);
            ReadOnlySpan<byte> v = payload.GetPlane(2, out int vStride);
            for (int y = 0; y < chromaHeight; y++)
            {
                Interleave(u.Slice(y * uStride, chromaWidth * sourceSample), v.Slice(y * vStride, chromaWidth * sourceSample),
                    chroma.AsSpan(y * chromaWidth * 2 * sample, chromaWidth * 2 * sample), deep, shift);
            }
        }
        else
        {
            ReadOnlySpan<byte> uv = payload.GetPlane(1, out int uvStride);
            for (int y = 0; y < chromaHeight; y++)
            {
                Widen(uv.Slice(y * uvStride, chromaWidth * 2 * sourceSample), chroma.AsSpan(y * chromaWidth * 2 * sample, chromaWidth * 2 * sample),
                    deep, shift);
            }
        }

        GpuTexture lumaTexture = gpu.CreateTexture(new GpuTextureDesc(width, height, GpuFormat.R16Unorm,
            GpuTextureUsage.Sampled), luma);
        try
        {
            GpuTexture chromaTexture = gpu.CreateTexture(new GpuTextureDesc(chromaWidth, chromaHeight, GpuFormat.Rg16Unorm,
                GpuTextureUsage.Sampled), chroma);
            FramePixelFormat format = payload.PixelFormat switch
            {
                FramePixelFormat.Yuv420P => FramePixelFormat.Nv12,
                FramePixelFormat.Yuv420P10 => FramePixelFormat.P010,
                _ => payload.PixelFormat,
            };
            return new GpuTextureSurface(lumaTexture, chromaTexture, format, payload.Color);
        }
        catch
        {
            lumaTexture.Dispose();
            throw;
        }
    }

    private GpuTextureSurface UploadRgba(CpuFramePayload payload)
    {
        int bytes = payload.PixelFormat == FramePixelFormat.Rgba16 ? 8 : 4;
        int row = payload.Width * bytes;
        ReadOnlySpan<byte> source = payload.GetPlane(0, out int stride);
        byte[] pixels = new byte[row * payload.Height];
        for (int y = 0; y < payload.Height; y++)
        {
            source.Slice(y * stride, row).CopyTo(pixels.AsSpan(y * row, row));
        }

        GpuTexture texture = gpu.CreateTexture(new GpuTextureDesc(payload.Width, payload.Height,
            bytes == 8 ? GpuFormat.Rgba16Unorm : GpuFormat.Bgra8Unorm, GpuTextureUsage.Sampled), pixels);
        return new GpuTextureSurface(texture, null, payload.PixelFormat, payload.Color);
    }

    private static void Widen(ReadOnlySpan<byte> source, Span<byte> target, bool deep, int shift)
    {
        if (!deep)
        {
            Span<ushort> words = MemoryMarshal.Cast<byte, ushort>(target);
            for (int i = 0; i < source.Length; i++)
            {
                words[i] = (ushort)(source[i] * 257);
            }

            return;
        }

        if (shift == 0)
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
            Span<ushort> words = MemoryMarshal.Cast<byte, ushort>(target);
            for (int i = 0; i < u.Length; i++)
            {
                words[2 * i] = (ushort)(u[i] * 257);
                words[(2 * i) + 1] = (ushort)(v[i] * 257);
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
