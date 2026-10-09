using System.Buffers;
using FFmpeg.AutoGen.Abstractions;
using Rodaje.Media.Gpu;
using DXGI_FORMAT = TerraFX.Interop.DirectX.DXGI_FORMAT;

namespace Rodaje.Media.Video;

/// <summary>Sube un cuadro decodificado en la CPU a una textura NV12 o P010, como las de D3D11VA.</summary>
/// <remarks>
/// Así el renderizador tiene un solo camino de muestreo. La textura se redondea a dimensiones pares
/// (requisito de NV12) repitiendo la última columna o fila. Otros formatos pasan antes por swscale.
/// </remarks>
internal sealed unsafe class FrameUploader : IDisposable
{
    private SwsContext* _scaler;
    private AVFrame* _converted = ffmpeg.av_frame_alloc();

    public static bool CanUpload(GpuDevice device, AVFrame* frame) =>
        TargetIsTenBit(frame) ? device.SupportsP010Textures : device.SupportsNv12Textures;

    public D3D11TexturePayload Upload(GpuDevice device, AVFrame* frame)
    {
        var format = (AVPixelFormat)frame->format;
        bool tenBit = TargetIsTenBit(frame);
        AVFrame* source = frame;
        if (format is not (AVPixelFormat.AV_PIX_FMT_YUV420P or AVPixelFormat.AV_PIX_FMT_YUVJ420P or AVPixelFormat.AV_PIX_FMT_NV12
            or AVPixelFormat.AV_PIX_FMT_YUV420P10LE or AVPixelFormat.AV_PIX_FMT_P010LE))
        {
            source = Convert(frame, tenBit ? AVPixelFormat.AV_PIX_FMT_P010LE : AVPixelFormat.AV_PIX_FMT_NV12);
        }

        int width = source->width;
        int height = source->height;
        int evenWidth = (width + 1) & ~1;
        int evenHeight = (height + 1) & ~1;
        int bytesPerSample = tenBit ? 2 : 1;
        int pitch = evenWidth * bytesPerSample;
        int size = pitch * evenHeight * 3 / 2;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(size);
        try
        {
            fixed (byte* destination = buffer)
            {
                if (tenBit)
                {
                    PackP010(source, (ushort*)destination, evenWidth, evenHeight);
                }
                else
                {
                    PackNv12(source, destination, evenWidth, evenHeight);
                }

                var texture = device.CreateTexture(tenBit ? DXGI_FORMAT.DXGI_FORMAT_P010 : DXGI_FORMAT.DXGI_FORMAT_NV12,
                    (uint)evenWidth, (uint)evenHeight, destination, (uint)pitch);
                return D3D11TexturePayload.FromOwnedTexture(device, texture, width, height);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            if (source != frame)
            {
                ffmpeg.av_frame_unref(source);
            }
        }
    }

    public void Dispose()
    {
        ffmpeg.sws_freeContext(_scaler);
        _scaler = null;
        AVFrame* converted = _converted;
        ffmpeg.av_frame_free(&converted);
        _converted = null;
    }

    private static bool TargetIsTenBit(AVFrame* frame)
    {
        AVPixFmtDescriptor* desc = ffmpeg.av_pix_fmt_desc_get((AVPixelFormat)frame->format);
        return desc != null && desc->comp[0].depth > 8;
    }

    private static void PackNv12(AVFrame* f, byte* dst, int evenWidth, int evenHeight)
    {
        int w = f->width;
        int h = f->height;
        for (int y = 0; y < evenHeight; y++)
        {
            byte* src = f->data[0] + (long)Math.Min(y, h - 1) * f->linesize[0];
            byte* row = dst + (long)y * evenWidth;
            new ReadOnlySpan<byte>(src, w).CopyTo(new Span<byte>(row, w));
            if (evenWidth > w)
            {
                row[w] = src[w - 1];
            }
        }

        byte* uv = dst + (long)evenWidth * evenHeight;
        int chromaWidth = evenWidth / 2;
        bool interleaved = (AVPixelFormat)f->format == AVPixelFormat.AV_PIX_FMT_NV12;
        for (int y = 0; y < evenHeight / 2; y++)
        {
            byte* row = uv + (long)y * evenWidth;
            if (interleaved)
            {
                new ReadOnlySpan<byte>(f->data[1] + (long)y * f->linesize[1], evenWidth).CopyTo(new Span<byte>(row, evenWidth));
                continue;
            }

            byte* u = f->data[1] + (long)y * f->linesize[1];
            byte* v = f->data[2] + (long)y * f->linesize[2];
            for (int x = 0; x < chromaWidth; x++)
            {
                row[2 * x] = u[x];
                row[(2 * x) + 1] = v[x];
            }
        }
    }

    private static void PackP010(AVFrame* f, ushort* dst, int evenWidth, int evenHeight)
    {
        int w = f->width;
        int h = f->height;
        bool alreadyHigh = (AVPixelFormat)f->format == AVPixelFormat.AV_PIX_FMT_P010LE;
        int shift = alreadyHigh ? 0 : 6;
        for (int y = 0; y < evenHeight; y++)
        {
            var src = (ushort*)(f->data[0] + (long)Math.Min(y, h - 1) * f->linesize[0]);
            ushort* row = dst + (long)y * evenWidth;
            for (int x = 0; x < evenWidth; x++)
            {
                row[x] = (ushort)(src[Math.Min(x, w - 1)] << shift);
            }
        }

        ushort* uv = dst + (long)evenWidth * evenHeight;
        int chromaWidth = evenWidth / 2;
        for (int y = 0; y < evenHeight / 2; y++)
        {
            ushort* row = uv + (long)y * evenWidth;
            if (alreadyHigh)
            {
                new ReadOnlySpan<ushort>(f->data[1] + (long)y * f->linesize[1], evenWidth).CopyTo(new Span<ushort>(row, evenWidth));
                continue;
            }

            var u = (ushort*)(f->data[1] + (long)y * f->linesize[1]);
            var v = (ushort*)(f->data[2] + (long)y * f->linesize[2]);
            for (int x = 0; x < chromaWidth; x++)
            {
                row[2 * x] = (ushort)(u[x] << 6);
                row[(2 * x) + 1] = (ushort)(v[x] << 6);
            }
        }
    }

    private AVFrame* Convert(AVFrame* frame, AVPixelFormat target)
    {
        _scaler = ffmpeg.sws_getCachedContext(_scaler, frame->width, frame->height, (AVPixelFormat)frame->format,
            frame->width, frame->height, target, (int)SwsFlags.SWS_BICUBIC, null, null, null);
        if (_scaler == null)
        {
            throw new MediaException($"swscale no puede convertir {Interop.Av.PixelFormatName((AVPixelFormat)frame->format)} a {Interop.Av.PixelFormatName(target)}.");
        }

        ffmpeg.av_frame_unref(_converted);
        _converted->format = (int)target;
        _converted->width = frame->width;
        _converted->height = frame->height;
        Interop.Av.Check(ffmpeg.av_frame_get_buffer(_converted, 0), "av_frame_get_buffer");
        Interop.Av.Check(ffmpeg.sws_scale(_scaler, frame->data, frame->linesize, 0, frame->height, _converted->data, _converted->linesize),
            "sws_scale");
        return _converted;
    }
}
