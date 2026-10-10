using FFmpeg.AutoGen.Abstractions;

namespace Rodaje.Media.Video;

/// <summary>Matriz con la que se pasa de Y'CbCr a R'G'B'.</summary>
public enum YuvMatrix
{
    Bt601,
    Bt709,
    Bt2020,
}

/// <summary>Cómo interpretar las muestras Y'CbCr de un cuadro: matriz y rango (limitado o completo).</summary>
public readonly record struct VideoColor(YuvMatrix Matrix, bool FullRange)
{
    /// <summary>Lo más común en video de alta definición: BT.709 en rango limitado.</summary>
    public static VideoColor Bt709Limited { get; } = new(YuvMatrix.Bt709, false);

    /// <summary>
    /// Lo que declara el flujo o el cuadro y, si no lo declara, lo habitual: BT.601 hasta 576 líneas y BT.709
    /// por encima; rango limitado salvo en los formatos <c>yuvj</c>, que son de rango completo.
    /// </summary>
    internal static VideoColor Resolve(AVColorSpace space, AVColorRange range, AVPixelFormat format, int height)
    {
        YuvMatrix matrix = space switch
        {
            AVColorSpace.AVCOL_SPC_BT709 or AVColorSpace.AVCOL_SPC_SMPTE240M => YuvMatrix.Bt709,
            AVColorSpace.AVCOL_SPC_BT470BG or AVColorSpace.AVCOL_SPC_SMPTE170M or AVColorSpace.AVCOL_SPC_FCC
                => YuvMatrix.Bt601,
            AVColorSpace.AVCOL_SPC_BT2020_NCL or AVColorSpace.AVCOL_SPC_BT2020_CL => YuvMatrix.Bt2020,
            _ => height is > 0 and <= 576 ? YuvMatrix.Bt601 : YuvMatrix.Bt709,
        };
        bool full = range switch
        {
            AVColorRange.AVCOL_RANGE_JPEG => true,
            AVColorRange.AVCOL_RANGE_MPEG => false,
            _ => format is AVPixelFormat.AV_PIX_FMT_YUVJ420P or AVPixelFormat.AV_PIX_FMT_YUVJ422P
                or AVPixelFormat.AV_PIX_FMT_YUVJ444P or AVPixelFormat.AV_PIX_FMT_YUVJ440P or AVPixelFormat.AV_PIX_FMT_YUVJ411P,
        };
        return new VideoColor(matrix, full);
    }

    /// <summary>Lo que dice el cuadro y, en lo que no diga, lo que dice el flujo.</summary>
    internal static unsafe VideoColor Resolve(AVFrame* frame, AVColorSpace streamSpace, AVColorRange streamRange)
    {
        AVColorSpace space = frame->colorspace != AVColorSpace.AVCOL_SPC_UNSPECIFIED ? frame->colorspace : streamSpace;
        AVColorRange range = frame->color_range != AVColorRange.AVCOL_RANGE_UNSPECIFIED ? frame->color_range : streamRange;
        return Resolve(space, range, (AVPixelFormat)frame->format, frame->height);
    }
}
