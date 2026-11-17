using FFmpeg.AutoGen.Abstractions;

namespace Rodaje.Media.Video;

/// <summary>Contenido de un cuadro decodificado en la CPU, cuando no hay dispositivo D3D11.</summary>
/// <remarks>El renderizador lo sube a una textura; los planos son válidos hasta <see cref="Dispose"/>.</remarks>
public sealed unsafe class CpuFramePayload : IDisposable
{
    private static long s_live;
    private AVFrame* _frame;

    /// <param name="format">El formato si no es el que dice FFmpeg (BGRA o RGBA de 16 bits ya premultiplicado).</param>
    internal CpuFramePayload(AVFrame* frame, VideoColor color, FramePixelFormat? format = null)
    {
        _frame = frame;
        Color = color;
        Width = frame->width;
        Height = frame->height;
        PixelFormat = format ?? (AVPixelFormat)frame->format switch
        {
            AVPixelFormat.AV_PIX_FMT_YUV420P or AVPixelFormat.AV_PIX_FMT_YUVJ420P => FramePixelFormat.Yuv420P,
            AVPixelFormat.AV_PIX_FMT_YUV420P10LE => FramePixelFormat.Yuv420P10,
            AVPixelFormat.AV_PIX_FMT_NV12 => FramePixelFormat.Nv12,
            AVPixelFormat.AV_PIX_FMT_P010LE => FramePixelFormat.P010,
            AVPixelFormat.AV_PIX_FMT_P016LE => FramePixelFormat.P016,
            _ => FramePixelFormat.Other,
        };
        FfmpegPixelFormat = Interop.Av.PixelFormatName((AVPixelFormat)frame->format);
        Interlocked.Increment(ref s_live);
    }

    public int Width { get; }

    public int Height { get; }

    public FramePixelFormat PixelFormat { get; }

    /// <summary>Matriz y rango con que se deben convertir las muestras a RGB.</summary>
    public VideoColor Color { get; }

    /// <summary>Nombre del formato según FFmpeg, útil cuando <see cref="PixelFormat"/> es <c>Other</c>.</summary>
    public string FfmpegPixelFormat { get; }

    public bool IsDisposed => _frame == null;

    internal static long LiveCount => Interlocked.Read(ref s_live);

    /// <summary>Bytes del plano <paramref name="plane"/> y la distancia en bytes entre filas.</summary>
    public ReadOnlySpan<byte> GetPlane(int plane, out int stride)
    {
        ObjectDisposedException.ThrowIf(_frame == null, this);
        ArgumentOutOfRangeException.ThrowIfNegative(plane);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(plane, 3);
        if (_frame->data[(uint)plane] == null)
        {
            throw new ArgumentOutOfRangeException(nameof(plane), $"El cuadro no tiene plano {plane}.");
        }

        stride = _frame->linesize[(uint)plane];
        int rows = plane == 0 ? Height : PlaneRows(plane);
        return new ReadOnlySpan<byte>(_frame->data[(uint)plane], stride * rows);
    }

    /// <summary>Cómo está la luma en el plano 0 de un formato YUV cualquiera (4:2:2, 4:4:4, con alfa, 10 o 12 bits).</summary>
    public bool TryGetLumaLayout(out int depth, out int step, out int offset)
    {
        ObjectDisposedException.ThrowIf(_frame == null, this);
        AVPixFmtDescriptor* desc = ffmpeg.av_pix_fmt_desc_get((AVPixelFormat)_frame->format);
        bool yuv = desc != null && (desc->flags & (ffmpeg.AV_PIX_FMT_FLAG_RGB | ffmpeg.AV_PIX_FMT_FLAG_BE | ffmpeg.AV_PIX_FMT_FLAG_HWACCEL)) == 0
            && desc->nb_components >= 3 && desc->comp[0].plane == 0 && desc->comp[0].shift == 0;
        depth = yuv ? desc->comp[0].depth : 0;
        step = yuv ? desc->comp[0].step : 0;
        offset = yuv ? desc->comp[0].offset : 0;
        return yuv && depth is >= 8 and <= 16;
    }

    /// <summary>
    /// Una copia que el compositor sabe subir, como hace <c>FrameUploader</c> en Windows: Y'CbCr a NV12, P010 o P016 (4:2:2, 4:4:4, más
    /// de 10 bits…) y lo que es RGB o tiene alfa a BGRA o RGBA de 16 bits con el alfa premultiplicado. La copia es del llamador.
    /// </summary>
    /// <exception cref="MediaException">swscale no sabe convertir el formato.</exception>
    public CpuFramePayload ToUploadable()
    {
        ObjectDisposedException.ThrowIf(_frame == null, this);
        var source = (AVPixelFormat)_frame->format;
        AVPixFmtDescriptor* desc = ffmpeg.av_pix_fmt_desc_get(source);
        if (desc == null || (desc->flags & ffmpeg.AV_PIX_FMT_FLAG_HWACCEL) != 0)
        {
            throw new MediaException($"El cuadro de CPU en {FfmpegPixelFormat} no se puede subir a la GPU.");
        }

        bool rgba = (desc->flags & (ffmpeg.AV_PIX_FMT_FLAG_RGB | ffmpeg.AV_PIX_FMT_FLAG_ALPHA)) != 0;
        int depth = desc->comp[0].depth;
        (AVPixelFormat target, FramePixelFormat format) = rgba
            ? depth > 8 ? (AVPixelFormat.AV_PIX_FMT_RGBA64LE, FramePixelFormat.Rgba16) : (AVPixelFormat.AV_PIX_FMT_BGRA, FramePixelFormat.Bgra8)
            : depth switch
            {
                <= 8 => (AVPixelFormat.AV_PIX_FMT_NV12, FramePixelFormat.Nv12),
                <= 10 => (AVPixelFormat.AV_PIX_FMT_P010LE, FramePixelFormat.P010),
                _ => (AVPixelFormat.AV_PIX_FMT_P016LE, FramePixelFormat.P016),
            };
        SwsContext* scaler = ffmpeg.sws_getContext(Width, Height, source, Width, Height, target, (int)SwsFlags.SWS_BICUBIC, null, null, null);
        if (scaler == null)
        {
            throw new MediaException($"swscale no puede convertir {FfmpegPixelFormat} a {Interop.Av.PixelFormatName(target)}.");
        }

        AVFrame* converted = ffmpeg.av_frame_alloc();
        try
        {
            converted->format = (int)target;
            converted->width = Width;
            converted->height = Height;
            Interop.Av.Check(ffmpeg.av_frame_get_buffer(converted, 0), "av_frame_get_buffer");
            Interop.Av.Check(ffmpeg.sws_scale(scaler, _frame->data, _frame->linesize, 0, Height, converted->data, converted->linesize), "sws_scale");
            if (rgba)
            {
                Premultiply(converted, depth > 8);
            }

            var result = new CpuFramePayload(converted, rgba ? Color : ConvertedColor(source, Color), format);
            converted = null;
            return result;
        }
        finally
        {
            ffmpeg.sws_freeContext(scaler);
            if (converted != null)
            {
                ffmpeg.av_frame_free(&converted);
            }
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

    // El filtro bilineal debe interpolar color premultiplicado: el RGB oculto por alfa cero no crea halos (como FrameUploader.UploadImage).
    private static void Premultiply(AVFrame* frame, bool deep)
    {
        for (int y = 0; y < frame->height; y++)
        {
            byte* row = frame->data[0] + ((long)y * frame->linesize[0]);
            for (int x = 0; x < frame->width; x++)
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
    }

    public void Dispose()
    {
        AVFrame* frame = _frame;
        if (frame == null)
        {
            return;
        }

        _frame = null;
        ffmpeg.av_frame_free(&frame);
        Interlocked.Decrement(ref s_live);
    }

    private int PlaneRows(int plane)
    {
        AVPixFmtDescriptor* desc = ffmpeg.av_pix_fmt_desc_get((AVPixelFormat)_frame->format);
        return plane is 1 or 2 && desc != null ? -((-Height) >> desc->log2_chroma_h) : Height;
    }
}
