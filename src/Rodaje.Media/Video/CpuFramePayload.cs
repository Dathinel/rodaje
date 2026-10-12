using FFmpeg.AutoGen.Abstractions;

namespace Rodaje.Media.Video;

/// <summary>Contenido de un cuadro decodificado en la CPU, cuando no hay dispositivo D3D11.</summary>
/// <remarks>El renderizador lo sube a una textura; los planos son válidos hasta <see cref="Dispose"/>.</remarks>
public sealed unsafe class CpuFramePayload : IDisposable
{
    private static long s_live;
    private AVFrame* _frame;

    internal CpuFramePayload(AVFrame* frame, VideoColor color)
    {
        _frame = frame;
        Color = color;
        Width = frame->width;
        Height = frame->height;
        PixelFormat = (AVPixelFormat)frame->format switch
        {
            AVPixelFormat.AV_PIX_FMT_YUV420P or AVPixelFormat.AV_PIX_FMT_YUVJ420P => FramePixelFormat.Yuv420P,
            AVPixelFormat.AV_PIX_FMT_YUV420P10LE => FramePixelFormat.Yuv420P10,
            AVPixelFormat.AV_PIX_FMT_NV12 => FramePixelFormat.Nv12,
            AVPixelFormat.AV_PIX_FMT_P010LE => FramePixelFormat.P010,
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
