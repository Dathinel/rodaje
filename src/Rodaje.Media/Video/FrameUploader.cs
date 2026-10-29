using System.Buffers;
using FFmpeg.AutoGen.Abstractions;
using Rodaje.Media.Gpu;
using DXGI_FORMAT = TerraFX.Interop.DirectX.DXGI_FORMAT;

namespace Rodaje.Media.Video;

/// <summary>Sube video de CPU a NV12/P010/P016, o una imagen fija a BGRA o RGBA16 premultiplicado con alfa.</summary>
/// <remarks>
/// Así el renderizador tiene un solo camino de muestreo. La textura se redondea a dimensiones pares
/// (requisito de NV12) repitiendo la última columna o fila. 4:2:0 y 4:2:2 planos de 8 y 10 bits se empaquetan
/// directamente, en paralelo por bandas; los demás formatos pasan antes por swscale (lo de más de 10 bits, a P016).
/// </remarks>
internal sealed unsafe class FrameUploader : IDisposable
{
    private SwsContext* _scaler;
    private AVFrame* _converted = ffmpeg.av_frame_alloc();

    public static bool CanUpload(GpuDevice device, AVFrame* frame) =>
        TargetDepth(frame) > 8 ? device.SupportsP010Textures : device.SupportsNv12Textures;

    public D3D11TexturePayload UploadImage(GpuDevice device, AVFrame* frame)
    {
        double scale = Math.Min(1.0, 8192.0 / Math.Max(frame->width, frame->height));
        int width = Math.Max(1, (int)Math.Round(frame->width * scale));
        int height = Math.Max(1, (int)Math.Round(frame->height * scale));
        bool deep = SampleDepth(frame) > 8;
        AVFrame* source = Convert(frame, deep ? AVPixelFormat.AV_PIX_FMT_RGBA64LE : AVPixelFormat.AV_PIX_FMT_BGRA, width, height);
        try
        {
            // El filtro bilineal debe interpolar color premultiplicado: el RGB oculto por alfa cero no crea halos.
            for (int y = 0; y < source->height; y++)
            {
                byte* row = source->data[0] + ((long)y * source->linesize[0]);
                for (int x = 0; x < source->width; x++)
                {
                    if (deep)
                    {
                        var pixel = (ushort*)row + (x * 4);
                        for (int c = 0; c < 3; c++)
                        {
                            pixel[c] = (ushort)(((uint)pixel[c] * pixel[3] + 32767) / 65535);
                        }
                    }
                    else
                    {
                        byte* pixel = row + (x * 4);
                        for (int c = 0; c < 3; c++)
                        {
                            pixel[c] = (byte)((pixel[c] * pixel[3] + 127) / 255);
                        }
                    }
                }
            }

            var texture = device.CreateTexture(deep ? DXGI_FORMAT.DXGI_FORMAT_R16G16B16A16_UNORM : DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
                (uint)source->width, (uint)source->height, source->data[0], (uint)source->linesize[0]);
            return D3D11TexturePayload.FromOwnedTexture(device, texture, source->width, source->height, VideoColor.Bt709Limited);
        }
        finally
        {
            ffmpeg.av_frame_unref(source);
        }
    }

    /// <summary>Sube el cuadro; <paramref name="color"/> dice cómo leer sus muestras y se ajusta si hay conversión.</summary>
    public D3D11TexturePayload Upload(GpuDevice device, AVFrame* frame, VideoColor color)
    {
        var format = (AVPixelFormat)frame->format;
        int depth = TargetDepth(frame);
        AVFrame* source = frame;
        if (depth == 16 || !PacksDirectly(format))
        {
            source = Convert(frame, depth switch { 16 => AVPixelFormat.AV_PIX_FMT_P016LE, 10 => AVPixelFormat.AV_PIX_FMT_P010LE, _ => AVPixelFormat.AV_PIX_FMT_NV12 });
            color = ConvertedColor(format, color);
        }

        int width = source->width;
        int height = source->height;
        int evenWidth = (width + 1) & ~1;
        int evenHeight = (height + 1) & ~1;
        int bytesPerSample = depth > 8 ? 2 : 1;
        int pitch = evenWidth * bytesPerSample;
        int size = pitch * evenHeight * 3 / 2;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(size);
        try
        {
            fixed (byte* destination = buffer)
            {
                if (depth > 8)
                {
                    PackP010(source, (ushort*)destination, evenWidth, evenHeight);
                }
                else
                {
                    PackNv12(source, destination, evenWidth, evenHeight);
                }

                // 16 bits: P016 donde la GPU lo muestrea y, si no, la misma palabra en una textura P010 (FramePixelFormat.P016).
                DXGI_FORMAT dxgi = depth switch
                {
                    16 when device.SupportsP016Textures => DXGI_FORMAT.DXGI_FORMAT_P016,
                    > 8 => DXGI_FORMAT.DXGI_FORMAT_P010,
                    _ => DXGI_FORMAT.DXGI_FORMAT_NV12,
                };
                var texture = device.CreateTexture(dxgi, (uint)evenWidth, (uint)evenHeight, destination, (uint)pitch);
                return D3D11TexturePayload.FromOwnedTexture(device, texture, width, height, color, depth == 16 ? FramePixelFormat.P016 : null);
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

    private static int SampleDepth(AVFrame* frame)
    {
        AVPixFmtDescriptor* desc = ffmpeg.av_pix_fmt_desc_get((AVPixelFormat)frame->format);
        return desc == null ? 8 : desc->comp[0].depth;
    }

    /// <summary>Profundidad de la textura: 8 (NV12), 10 (P010) o 16 (P016, para 12 bits o más).</summary>
    private static int TargetDepth(AVFrame* frame) => SampleDepth(frame) switch { <= 8 => 8, <= 10 => 10, _ => 16 };

    // Formatos de 8 y 10 bits que se empaquetan sin pasar por swscale: 4:2:0 y 4:2:2 planos (el 4:2:2 promedia dos filas de croma).
    private static bool PacksDirectly(AVPixelFormat format) => format is AVPixelFormat.AV_PIX_FMT_YUV420P or AVPixelFormat.AV_PIX_FMT_YUVJ420P
        or AVPixelFormat.AV_PIX_FMT_NV12 or AVPixelFormat.AV_PIX_FMT_YUV420P10LE or AVPixelFormat.AV_PIX_FMT_P010LE
        or AVPixelFormat.AV_PIX_FMT_YUV422P or AVPixelFormat.AV_PIX_FMT_YUV422P10LE;

    // Por bandas de filas en paralelo: en 4K un solo hilo tardaba más que decodificar (ProRes 4K: 6 fps con la subida
    // frente a 45 sin ella, medido en la RTX 3050 con 16 hilos).
    private static void ForBands(int rows, Action<int, int> body)
    {
        int bands = Math.Clamp(rows / 128, 1, Environment.ProcessorCount);
        if (bands == 1)
        {
            body(0, rows);
            return;
        }

        int step = (rows + bands - 1) / bands;
        Parallel.For(0, bands, band => body(band * step, Math.Min(rows, (band + 1) * step)));
    }

    private static void PackNv12(AVFrame* f, byte* dst, int evenWidth, int evenHeight)
    {
        int w = f->width;
        int h = f->height;
        var format = (AVPixelFormat)f->format;
        bool interleaved = format == AVPixelFormat.AV_PIX_FMT_NV12;
        bool chroma422 = format == AVPixelFormat.AV_PIX_FMT_YUV422P;
        int chromaRows = chroma422 ? h : (h + 1) / 2;
        nint frame = (nint)f;
        nint target = (nint)dst;
        ForBands(evenHeight, (from, to) =>
        {
            var source = (AVFrame*)frame;
            byte* destination = (byte*)target;
            for (int y = from; y < to; y++)
            {
                byte* src = source->data[0] + (long)Math.Min(y, h - 1) * source->linesize[0];
                byte* row = destination + (long)y * evenWidth;
                new ReadOnlySpan<byte>(src, w).CopyTo(new Span<byte>(row, w));
                if (evenWidth > w)
                {
                    row[w] = src[w - 1];
                }
            }

            byte* uv = destination + (long)evenWidth * evenHeight;
            int chromaWidth = evenWidth / 2;
            for (int y = (from + 1) / 2; y < (to + 1) / 2 && y < evenHeight / 2; y++)
            {
                byte* row = uv + (long)y * evenWidth;
                if (interleaved)
                {
                    new ReadOnlySpan<byte>(source->data[1] + (long)y * source->linesize[1], evenWidth).CopyTo(new Span<byte>(row, evenWidth));
                    continue;
                }

                (int a, int b) = ChromaRows(y, chroma422, chromaRows);
                byte* ua = source->data[1] + (long)a * source->linesize[1];
                byte* ub = source->data[1] + (long)b * source->linesize[1];
                byte* va = source->data[2] + (long)a * source->linesize[2];
                byte* vb = source->data[2] + (long)b * source->linesize[2];
                var pairs = (ushort*)row;
                for (int x = 0; x < chromaWidth; x++)
                {
                    int u = (ua[x] + ub[x] + 1) >> 1;
                    int v = (va[x] + vb[x] + 1) >> 1;
                    pairs[x] = (ushort)(u | (v << 8));
                }
            }
        });
    }

    private static void PackP010(AVFrame* f, ushort* dst, int evenWidth, int evenHeight)
    {
        int w = f->width;
        int h = f->height;
        var format = (AVPixelFormat)f->format;
        bool alreadyHigh = format is AVPixelFormat.AV_PIX_FMT_P010LE or AVPixelFormat.AV_PIX_FMT_P016LE;
        bool chroma422 = format == AVPixelFormat.AV_PIX_FMT_YUV422P10LE;
        int chromaRows = chroma422 ? h : (h + 1) / 2;
        int shift = alreadyHigh ? 0 : 6;
        nint frame = (nint)f;
        nint target = (nint)dst;
        ForBands(evenHeight, (from, to) =>
        {
            var source = (AVFrame*)frame;
            var destination = (ushort*)target;
            for (int y = from; y < to; y++)
            {
                var src = (ushort*)(source->data[0] + (long)Math.Min(y, h - 1) * source->linesize[0]);
                ushort* row = destination + (long)y * evenWidth;
                ShiftRow(src, row, w, shift);
                if (evenWidth > w)
                {
                    row[w] = row[w - 1];
                }
            }

            ushort* uv = destination + (long)evenWidth * evenHeight;
            int chromaWidth = evenWidth / 2;
            for (int y = (from + 1) / 2; y < (to + 1) / 2 && y < evenHeight / 2; y++)
            {
                ushort* row = uv + (long)y * evenWidth;
                if (alreadyHigh)
                {
                    new ReadOnlySpan<ushort>(source->data[1] + (long)y * source->linesize[1], evenWidth).CopyTo(new Span<ushort>(row, evenWidth));
                    continue;
                }

                (int a, int b) = ChromaRows(y, chroma422, chromaRows);
                var ua = (ushort*)(source->data[1] + (long)a * source->linesize[1]);
                var ub = (ushort*)(source->data[1] + (long)b * source->linesize[1]);
                var va = (ushort*)(source->data[2] + (long)a * source->linesize[2]);
                var vb = (ushort*)(source->data[2] + (long)b * source->linesize[2]);
                var pairs = (uint*)row;
                for (int x = 0; x < chromaWidth; x++)
                {
                    uint u = (uint)((ua[x] + ub[x] + 1) >> 1) << 6;
                    uint v = (uint)((va[x] + vb[x] + 1) >> 1) << 6;
                    pairs[x] = u | (v << 16);
                }
            }
        });
    }

    // Fila y de croma 4:2:0 → filas de origen: la misma dos veces (4:2:0) o las dos que cubre (4:2:2), sin salirse.
    private static (int A, int B) ChromaRows(int y, bool chroma422, int chromaRows) => chroma422
        ? (Math.Min(2 * y, chromaRows - 1), Math.Min((2 * y) + 1, chromaRows - 1))
        : (Math.Min(y, chromaRows - 1), Math.Min(y, chromaRows - 1));

    private static void ShiftRow(ushort* src, ushort* dst, int width, int shift)
    {
        var source = new ReadOnlySpan<ushort>(src, width);
        var target = new Span<ushort>(dst, width);
        if (shift == 0)
        {
            source.CopyTo(target);
            return;
        }

        int x = 0;
        if (System.Numerics.Vector.IsHardwareAccelerated)
        {
            int lanes = System.Numerics.Vector<ushort>.Count;
            for (; x <= width - lanes; x += lanes)
            {
                System.Numerics.Vector.ShiftLeft(new System.Numerics.Vector<ushort>(source[x..]), shift).CopyTo(target[x..]);
            }
        }

        for (; x < width; x++)
        {
            target[x] = (ushort)(source[x] << shift);
        }
    }

    /// <summary>
    /// swscale sin rangos explícitos lleva los <c>yuvj</c> a rango limitado, deja igual el resto de Y'CbCr y pasa
    /// RGB a BT.601 limitado.
    /// </summary>
    internal static VideoColor ConvertedColor(AVPixelFormat source, VideoColor color)
    {
        AVPixFmtDescriptor* desc = ffmpeg.av_pix_fmt_desc_get(source);
        if (desc != null && (desc->flags & (ulong)ffmpeg.AV_PIX_FMT_FLAG_RGB) != 0)
        {
            return color with { Matrix = YuvMatrix.Bt601, FullRange = false };
        }

        bool jpeg = source is AVPixelFormat.AV_PIX_FMT_YUVJ420P or AVPixelFormat.AV_PIX_FMT_YUVJ422P
            or AVPixelFormat.AV_PIX_FMT_YUVJ444P or AVPixelFormat.AV_PIX_FMT_YUVJ440P or AVPixelFormat.AV_PIX_FMT_YUVJ411P;
        return jpeg ? color with { FullRange = false } : color;
    }

    private AVFrame* Convert(AVFrame* frame, AVPixelFormat target, int width = 0, int height = 0)
    {
        width = width == 0 ? frame->width : width;
        height = height == 0 ? frame->height : height;
        _scaler = ffmpeg.sws_getCachedContext(_scaler, frame->width, frame->height, (AVPixelFormat)frame->format,
            width, height, target, (int)SwsFlags.SWS_BICUBIC, null, null, null);
        if (_scaler == null)
        {
            throw new MediaException($"swscale no puede convertir {Interop.Av.PixelFormatName((AVPixelFormat)frame->format)} a {Interop.Av.PixelFormatName(target)}.");
        }

        ffmpeg.av_frame_unref(_converted);
        _converted->format = (int)target;
        _converted->width = width;
        _converted->height = height;
        Interop.Av.Check(ffmpeg.av_frame_get_buffer(_converted, 0), "av_frame_get_buffer");
        Interop.Av.Check(ffmpeg.sws_scale(_scaler, frame->data, frame->linesize, 0, frame->height, _converted->data, _converted->linesize),
            "sws_scale");
        return _converted;
    }
}
