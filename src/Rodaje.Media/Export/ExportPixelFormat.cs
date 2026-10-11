namespace Rodaje.Media.Export;

/// <summary>Submuestreo del croma.</summary>
public enum ChromaSubsampling
{
    Chroma420,
    Chroma422,
    Chroma444,
}

/// <summary>Formato de los cuadros que baja la GPU y recibe el sink (D-028: Y'CbCr hecho en la GPU; RGB solo para imágenes, GIF y alfa).</summary>
public enum ExportPixelFormat
{
    /// <summary>4:2:0 de 8 bits, Y y luego CbCr entrelazado. Lo que piden NVENC, AMF, QSV y Media Foundation.</summary>
    Nv12,

    /// <summary>4:2:0 de 10 bits en palabras de 16 bits (los 10 de arriba), Y y luego CbCr entrelazado.</summary>
    P010,

    /// <summary>4:2:0 de 8 bits en tres planos (SVT-AV1, libvpx, FFV1, MJPEG).</summary>
    Yuv420P,

    /// <summary>4:2:0 de 10 bits en tres planos de palabras de 16 bits (los 10 de abajo).</summary>
    Yuv420P10,

    /// <summary>4:2:2 de 8 bits en tres planos (DNxHR SQ/HQ).</summary>
    Yuv422P,

    /// <summary>4:2:2 de 10 bits en tres planos (ProRes 422, DNxHR HQX, HEVC Main 4:2:2 10).</summary>
    Yuv422P10,

    Yuv444P,

    /// <summary>4:4:4 de 10 bits (ProRes 4444 sin alfa, DNxHR 444).</summary>
    Yuv444P10,

    /// <summary>4:2:0 de 8 bits con un cuarto plano de alfa a resolución completa (VP9 con alfa en WebM).</summary>
    Yuva420P,

    /// <summary>4:4:4 de 10 bits con alfa (ProRes 4444).</summary>
    Yuva444P10,

    /// <summary>RGB de 8 bits empaquetado (PNG, TIFF, GIF, WebP, JPEG de secuencias).</summary>
    Rgb24,

    /// <summary>RGBA de 8 bits empaquetado, alfa recto (PNG, QuickTime Animation, WebP con alfa).</summary>
    Rgba,

    /// <summary>RGBA de 16 bits por canal empaquetado, alfa recto (PNG y TIFF de 16 bits; EXR la convierte FFmpeg).</summary>
    Rgba64,
}

/// <summary>Geometría y nombres de <see cref="ExportPixelFormat"/>.</summary>
public static class ExportPixelFormats
{
    /// <summary>Nombre de <c>-pix_fmt</c> en FFmpeg.</summary>
    public static string FfmpegName(this ExportPixelFormat format) => format switch
    {
        ExportPixelFormat.Nv12 => "nv12",
        ExportPixelFormat.P010 => "p010le",
        ExportPixelFormat.Yuv420P => "yuv420p",
        ExportPixelFormat.Yuv420P10 => "yuv420p10le",
        ExportPixelFormat.Yuv422P => "yuv422p",
        ExportPixelFormat.Yuv422P10 => "yuv422p10le",
        ExportPixelFormat.Yuv444P => "yuv444p",
        ExportPixelFormat.Yuv444P10 => "yuv444p10le",
        ExportPixelFormat.Yuva420P => "yuva420p",
        ExportPixelFormat.Yuva444P10 => "yuva444p10le",
        ExportPixelFormat.Rgb24 => "rgb24",
        ExportPixelFormat.Rgba => "rgba",
        ExportPixelFormat.Rgba64 => "rgba64le",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Formato de píxel desconocido."),
    };

    public static bool IsRgb(this ExportPixelFormat format) =>
        format is ExportPixelFormat.Rgb24 or ExportPixelFormat.Rgba or ExportPixelFormat.Rgba64;

    public static bool HasAlpha(this ExportPixelFormat format) =>
        format is ExportPixelFormat.Yuva420P or ExportPixelFormat.Yuva444P10 or ExportPixelFormat.Rgba or ExportPixelFormat.Rgba64;

    public static int BitDepth(this ExportPixelFormat format) => format switch
    {
        ExportPixelFormat.Nv12 or ExportPixelFormat.Yuv420P or ExportPixelFormat.Yuv422P or ExportPixelFormat.Yuv444P
            or ExportPixelFormat.Yuva420P or ExportPixelFormat.Rgb24 or ExportPixelFormat.Rgba => 8,
        ExportPixelFormat.Rgba64 => 16,
        _ => 10,
    };

    /// <summary>Submuestreo del croma; los RGB cuentan como 4:4:4.</summary>
    public static ChromaSubsampling Chroma(this ExportPixelFormat format) => format switch
    {
        ExportPixelFormat.Nv12 or ExportPixelFormat.P010 or ExportPixelFormat.Yuv420P or ExportPixelFormat.Yuv420P10
            or ExportPixelFormat.Yuva420P => ChromaSubsampling.Chroma420,
        ExportPixelFormat.Yuv422P or ExportPixelFormat.Yuv422P10 => ChromaSubsampling.Chroma422,
        _ => ChromaSubsampling.Chroma444,
    };

    /// <summary>Bytes por muestra de un canal: 1 en 8 bits, 2 en 10 y 16.</summary>
    public static int BytesPerSample(this ExportPixelFormat format) => format.BitDepth() == 8 ? 1 : 2;

    /// <summary>1 en los RGB empaquetados, 2 en los semiplanares (NV12, P010), 3 en los planares y 4 con alfa.</summary>
    public static int PlaneCount(this ExportPixelFormat format) => format switch
    {
        _ when format.IsRgb() => 1,
        ExportPixelFormat.Nv12 or ExportPixelFormat.P010 => 2,
        ExportPixelFormat.Yuva420P or ExportPixelFormat.Yuva444P10 => 4,
        _ => 3,
    };

    /// <summary>Si el croma tiene la mitad de filas (4:2:0).</summary>
    public static bool HalfHeightChroma(this ExportPixelFormat format) => format.Chroma() == ChromaSubsampling.Chroma420;

    /// <summary>Si el ancho debe ser par (croma 4:2:0 o 4:2:2).</summary>
    public static bool NeedsEvenWidth(this ExportPixelFormat format) => format.Chroma() != ChromaSubsampling.Chroma444;

    /// <summary>Ancho en muestras y alto en filas del plano <paramref name="plane"/> de un cuadro de <paramref name="width"/>×<paramref name="height"/>.</summary>
    /// <remarks>
    /// En los semiplanares el plano 1 lleva Cb y Cr entrelazados: se cuenta como la mitad de muestras dobles, que en bytes es
    /// una fila de luma. En los RGB empaquetados el ancho son las muestras de todos los canales.
    /// </remarks>
    public static (int Width, int Height) PlaneSize(this ExportPixelFormat format, int plane, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(plane);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(plane, format.PlaneCount());
        if (format.IsRgb())
        {
            int channels = format == ExportPixelFormat.Rgb24 ? 3 : 4;
            return (width * channels, height);
        }

        if (plane == 0 || plane == 3)
        {
            return (width, height);
        }

        int chromaHeight = format.HalfHeightChroma() ? (height + 1) / 2 : height;
        int chromaWidth = format.Chroma() == ChromaSubsampling.Chroma444 ? width : (width + 1) / 2;
        return format.PlaneCount() == 2 ? (chromaWidth * 2, chromaHeight) : (chromaWidth, chromaHeight);
    }

    /// <summary>Bytes de una fila útil del plano (sin el relleno del pitch).</summary>
    public static int RowBytes(this ExportPixelFormat format, int plane, int width, int height) =>
        format.PlaneSize(plane, width, height).Width * format.BytesPerSample();

    /// <summary>Bytes de un cuadro entero, sin relleno: lo que viaja por la tubería.</summary>
    public static long FrameBytes(this ExportPixelFormat format, int width, int height)
    {
        long total = 0;
        for (int plane = 0; plane < format.PlaneCount(); plane++)
        {
            total += (long)format.RowBytes(plane, width, height) * format.PlaneSize(plane, width, height).Height;
        }

        return total;
    }
}
