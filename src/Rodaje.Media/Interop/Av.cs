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

    /// <summary>
    /// <c>AV_NOPTS_VALUE</c> de FFmpeg («sin marca»). Copia literal para que las conversiones puras se prueben sin cargar
    /// FFmpeg; <c>AvTimeTests</c> comprueba que coincide.
    /// </summary>
    public const long NoTimestamp = long.MinValue;

    /// <summary>
    /// Convierte una marca en la base de tiempo dada a <see cref="Time"/>, redondeando al tick. Devuelve
    /// <see langword="false"/> si no hay marca (<see cref="NoTimestamp"/>), si la base no es válida o si el instante no cabe
    /// en <see cref="Time"/> (una cabecera dañada con más de ~415 años): antes se truncaba a un instante inventado.
    /// </summary>
    public static bool TryToTime(long timestamp, AVRational timeBase, out Time time)
    {
        time = Time.Zero;
        if (timestamp == NoTimestamp || !IsValid(timeBase))
        {
            return false;
        }

        Int128 ticks = RoundDiv((Int128)timestamp * Time.TicksPerSecond * timeBase.num, timeBase.den);
        if (ticks < long.MinValue || ticks > long.MaxValue)
        {
            return false;
        }

        time = Time.FromTicks((long)ticks);
        return true;
    }

    /// <summary>
    /// Convierte un <see cref="Time"/> a la base de tiempo dada, redondeando al entero más cercano. Devuelve
    /// <see langword="false"/> si la base no es válida o si la marca no cabe en <see langword="long"/> o cae en
    /// <see cref="NoTimestamp"/> (con bases finas, como 1/1 000 000 000, los instantes de más de ~292 años no caben).
    /// </summary>
    public static bool TryFromTime(Time time, AVRational timeBase, out long timestamp)
    {
        timestamp = 0;
        if (!IsValid(timeBase))
        {
            return false;
        }

        Int128 value = RoundDiv((Int128)time.Ticks * timeBase.den, (Int128)Time.TicksPerSecond * timeBase.num);
        if (value <= NoTimestamp || value > long.MaxValue)
        {
            return false;
        }

        timestamp = (long)value;
        return true;
    }

    /// <summary>Como <see cref="TryToTime"/>, pero lanza <see cref="MediaException"/> si la marca no tiene instante válido.</summary>
    public static Time ToTime(long timestamp, AVRational timeBase) =>
        TryToTime(timestamp, timeBase, out Time time)
            ? time
            : throw new MediaException(
                $"La marca de tiempo {timestamp} en base {timeBase.num}/{timeBase.den} no es un instante válido; el archivo puede estar dañado.");

    /// <summary>Como <see cref="TryFromTime"/>, pero lanza <see cref="MediaException"/> si el instante no cabe en la base.</summary>
    public static long FromTime(Time time, AVRational timeBase) =>
        TryFromTime(time, timeBase, out long timestamp)
            ? timestamp
            : throw new MediaException(
                $"El instante {time} no cabe en la base de tiempo {timeBase.num}/{timeBase.den}; el archivo puede estar dañado.");

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
