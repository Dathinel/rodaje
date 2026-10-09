using System.Globalization;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen.Abstractions;
using Rodaje.Core.Timing;

namespace Rodaje.Media.Interop;

internal static unsafe class Av
{
    public static readonly int ErrorAgain = ffmpeg.AVERROR(ffmpeg.EAGAIN);

    public static int Check(int result, string operation)
    {
        if (result < 0)
        {
            throw new MediaException($"{operation} falló: {ErrorText(result)} ({result}).", result);
        }

        return result;
    }

    public static string ErrorText(int error)
    {
        const int Size = 256;
        byte* buffer = stackalloc byte[Size];
        if (ffmpeg.av_strerror(error, buffer, Size) < 0)
        {
            return error.ToString(CultureInfo.InvariantCulture);
        }

        return Marshal.PtrToStringUTF8((nint)buffer) ?? error.ToString(CultureInfo.InvariantCulture);
    }

    public static bool IsValid(AVRational r) => r.num > 0 && r.den > 0;

    /// <summary>Convierte una marca en la base de tiempo dada a <see cref="Time"/>, redondeando al tick.</summary>
    public static Time ToTime(long timestamp, AVRational timeBase)
    {
        Int128 numerator = (Int128)timestamp * Time.TicksPerSecond * timeBase.num;
        return Time.FromTicks((long)RoundDiv(numerator, timeBase.den));
    }

    /// <summary>Convierte un <see cref="Time"/> a la base de tiempo dada, redondeando al entero más cercano.</summary>
    public static long FromTime(Time time, AVRational timeBase)
    {
        Int128 numerator = (Int128)time.Ticks * timeBase.den;
        return (long)RoundDiv(numerator, (Int128)Time.TicksPerSecond * timeBase.num);
    }

    /// <summary>Redondeo al más cercano, con las mitades hacia +∞, válido para signos cualesquiera.</summary>
    public static Int128 RoundDiv(Int128 numerator, Int128 denominator)
    {
        if (denominator < 0)
        {
            numerator = -numerator;
            denominator = -denominator;
        }

        Int128 twice = numerator * 2 + denominator;
        Int128 q = twice / (denominator * 2);
        return (twice % (denominator * 2) != 0 && twice < 0) ? q - 1 : q;
    }

    public static Int128 FloorDiv(Int128 a, Int128 b)
    {
        Int128 q = a / b;
        return (a % b != 0 && (a < 0) != (b < 0)) ? q - 1 : q;
    }

    public static string CodecName(AVCodecID id) => ffmpeg.avcodec_get_name(id) ?? id.ToString();

    public static string PixelFormatName(AVPixelFormat format) =>
        format == AVPixelFormat.AV_PIX_FMT_NONE ? "ninguno" : ffmpeg.av_get_pix_fmt_name(format) ?? format.ToString();

    public static AVFormatContext* OpenInput(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"No existe el archivo de medio {path}.", path);
        }

        FfmpegLibrary.EnsureInitialized();
        AVFormatContext* format = null;
        int r = ffmpeg.avformat_open_input(&format, path, null, null);
        if (r < 0)
        {
            throw new MediaException($"No se pudo abrir {path}: {ErrorText(r)}.", r);
        }

        r = ffmpeg.avformat_find_stream_info(format, null);
        if (r < 0)
        {
            ffmpeg.avformat_close_input(&format);
            throw new MediaException($"No se pudo leer la información de los flujos de {path}: {ErrorText(r)}.", r);
        }

        return format;
    }
}
