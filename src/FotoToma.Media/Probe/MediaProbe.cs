using System.Runtime.InteropServices;
using FFmpeg.AutoGen.Abstractions;
using FotoToma.Core.Timing;
using FotoToma.Media.Interop;

namespace FotoToma.Media.Probe;

/// <summary>Lee los metadatos de un archivo de medio con FFmpeg.</summary>
public static unsafe class MediaProbe
{
    private const int VfrScanPackets = 240;

    // Tasas a las que se ajusta una medida muy cercana: 29,97 sale como 30000/1001 y no como 2997/100.
    private static readonly Rational[] StandardRates =
    [
        new(24000, 1001), new(24, 1), new(25, 1), new(30000, 1001), new(30, 1), new(48, 1), new(50, 1),
        new(60000, 1001), new(60, 1), new(90, 1), new(100, 1), new(120000, 1001), new(120, 1), new(144, 1),
        new(240, 1), new(15, 1), new(12, 1), new(10, 1), new(8, 1), new(5, 1),
    ];

    /// <summary>Abre el archivo, lee sus flujos y lo cierra.</summary>
    /// <exception cref="FfmpegNotFoundException">No se pudo cargar FFmpeg.</exception>
    /// <exception cref="MediaException">El archivo no se pudo abrir o no tiene audio ni video.</exception>
    public static MediaInfo Probe(string path)
    {
        AVFormatContext* format = Av.OpenInput(path);
        try
        {
            return Probe(format, path, scanTimestamps: true);
        }
        finally
        {
            ffmpeg.avformat_close_input(&format);
        }
    }

    /// <summary>Ajusta una tasa medida a la estándar más cercana si difiere menos de 0,05 %.</summary>
    public static Rational SnapFrameRate(Rational rate)
    {
        double value = rate.ToDouble();
        foreach (Rational standard in StandardRates)
        {
            if (Math.Abs(value - standard.ToDouble()) / standard.ToDouble() < 0.0005)
            {
                return standard;
            }
        }

        return rate;
    }

    /// <summary>Reglas de cordura: se ignoran tasas fuera de [1, 1000]; si avg y r coinciden se toma r.</summary>
    public static Rational? ChooseFrameRate(Rational? average, Rational? real)
    {
        static Rational? Sane(Rational? r) => r is { } v && v.ToDouble() is >= 1 and <= 1000 ? SnapFrameRate(v) : null;
        Rational? avg = Sane(average);
        Rational? r = Sane(real);
        if (avg is { } a && r is { } b)
        {
            return Math.Abs(a.ToDouble() - b.ToDouble()) / b.ToDouble() < 0.001 ? b : a;
        }

        return avg ?? r;
    }

    internal static MediaInfo Probe(AVFormatContext* format, string path, bool scanTimestamps)
    {
        int videoIndex = ffmpeg.av_find_best_stream(format, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
        int audioIndex = ffmpeg.av_find_best_stream(format, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, null, 0);
        if (videoIndex >= 0 && (format->streams[videoIndex]->disposition & ffmpeg.AV_DISPOSITION_ATTACHED_PIC) != 0)
        {
            videoIndex = -1;
        }

        if (videoIndex < 0 && audioIndex < 0)
        {
            throw new MediaException($"El archivo {path} no tiene flujos de video ni de audio que FotoToma pueda leer.");
        }

        AVRational microseconds = new() { num = 1, den = ffmpeg.AV_TIME_BASE };
        Time containerStart = format->start_time == ffmpeg.AV_NOPTS_VALUE ? Time.Zero : Av.ToTime(format->start_time, microseconds);
        Time? containerDuration = format->duration > 0 ? Av.ToTime(format->duration, microseconds) : null;

        VideoStreamInfo? video = videoIndex >= 0
            ? ReadVideo(format, videoIndex, containerStart, containerDuration, scanTimestamps)
            : null;
        AudioStreamInfo? audio = audioIndex >= 0 ? ReadAudio(format, audioIndex, containerStart, containerDuration) : null;

        Time origin = video?.StartTime ?? audio?.StartTime ?? containerStart;
        Time duration = Time.Zero;
        if (video is not null)
        {
            duration = video.StartTime + video.Duration - origin;
        }

        if (audio is not null)
        {
            duration = Time.Max(duration, audio.StartTime + audio.Duration - origin);
        }

        return new MediaInfo
        {
            Path = path,
            ContainerFormat = Marshal.PtrToStringUTF8((nint)format->iformat->name) ?? "",
            Duration = duration,
            ContainerStartTime = containerStart,
            Video = video,
            Audio = audio,
        };
    }

    private static Time StreamStart(AVStream* stream, Time containerStart) =>
        stream->start_time == ffmpeg.AV_NOPTS_VALUE ? containerStart : Av.ToTime(stream->start_time, stream->time_base);

    private static Time? StreamDuration(AVStream* stream, Time start, Time containerStart, Time? containerDuration)
    {
        if (stream->duration > 0 && Av.IsValid(stream->time_base))
        {
            return Av.ToTime(stream->duration, stream->time_base);
        }

        return containerDuration is { } d ? d - (start - containerStart) : null;
    }

    private static VideoStreamInfo ReadVideo(AVFormatContext* format, int index, Time containerStart, Time? containerDuration, bool scan)
    {
        AVStream* stream = format->streams[index];
        AVCodecParameters* par = stream->codecpar;
        var pixelFormat = (AVPixelFormat)par->format;
        AVPixFmtDescriptor* descriptor = ffmpeg.av_pix_fmt_desc_get(pixelFormat);
        int bitDepth = descriptor != null ? descriptor->comp[0].depth : 8;

        Rational? average = ToRational(stream->avg_frame_rate);
        Rational? real = ToRational(stream->r_frame_rate);
        Rational? chosen = ChooseFrameRate(average, real);
        bool vfr = average is { } a && real is { } r && Math.Abs(a.ToDouble() - r.ToDouble()) / r.ToDouble() > 0.02;
        if (scan)
        {
            (Rational? measured, bool irregular) = ScanTimestamps(format, index);
            vfr |= irregular;
            // Con tasa variable el promedio (29,87…) numeraría mal; el intervalo más común es la tasa nominal.
            if (measured is not null && (chosen is null || vfr))
            {
                chosen = measured;
            }
        }

        Rational rate = chosen ?? Rational.Fps30;
        Time start = StreamStart(stream, containerStart);
        Time? streamDuration = StreamDuration(stream, start, containerStart, containerDuration);
        long frameCount = stream->nb_frames > 0
            ? stream->nb_frames
            : streamDuration is { } sd ? Math.Max(1, (long)Av.RoundDiv((Int128)sd.Ticks * rate.Num, (Int128)Time.TicksPerSecond * rate.Den)) : 0;
        Time duration = vfr && streamDuration is { } vd ? vd : Time.FromFrames(frameCount, rate);

        Rational sar = ToRational(par->sample_aspect_ratio) ?? ToRational(stream->sample_aspect_ratio) ?? new Rational(1, 1);
        int rotation = ReadRotation(par);
        int displayWidth = (int)Math.Round(par->width * sar.ToDouble());
        int displayHeight = par->height;
        if (rotation is 90 or 270)
        {
            (displayWidth, displayHeight) = (displayHeight, displayWidth);
        }

        return new VideoStreamInfo
        {
            StreamIndex = index,
            Codec = Av.CodecName(par->codec_id),
            PixelFormat = Av.PixelFormatName(pixelFormat),
            BitDepth = bitDepth,
            Width = par->width,
            Height = par->height,
            SampleAspectRatio = sar,
            Rotation = rotation,
            DisplayWidth = displayWidth,
            DisplayHeight = displayHeight,
            FrameRate = rate,
            AverageFrameRate = average,
            RealFrameRate = real,
            IsVariableFrameRate = vfr,
            FrameCount = frameCount,
            Duration = duration,
            StartTime = start,
        };
    }

    private static AudioStreamInfo ReadAudio(AVFormatContext* format, int index, Time containerStart, Time? containerDuration)
    {
        AVStream* stream = format->streams[index];
        AVCodecParameters* par = stream->codecpar;
        Time start = StreamStart(stream, containerStart);
        return new AudioStreamInfo
        {
            StreamIndex = index,
            Codec = Av.CodecName(par->codec_id),
            SampleRate = par->sample_rate,
            Channels = par->ch_layout.nb_channels,
            Duration = StreamDuration(stream, start, containerStart, containerDuration) ?? Time.Zero,
            StartTime = start,
        };
    }

    private static Rational? ToRational(AVRational r) => Av.IsValid(r) ? new Rational(r.num, r.den) : null;

    private static int ReadRotation(AVCodecParameters* par)
    {
        AVPacketSideData* side = ffmpeg.av_packet_side_data_get(par->coded_side_data, par->nb_coded_side_data,
            AVPacketSideDataType.AV_PKT_DATA_DISPLAYMATRIX);
        if (side == null || side->size < 9 * sizeof(int))
        {
            return 0;
        }

        double counterClockwise = ffmpeg.av_display_rotation_get(*(int9*)side->data);
        if (double.IsNaN(counterClockwise))
        {
            return 0;
        }

        int clockwise = (int)Math.Round(-counterClockwise / 90.0) * 90;
        return ((clockwise % 360) + 360) % 360;
    }

    // Lee (sin decodificar) los primeros paquetes del flujo: los intervalos entre marcas de presentación
    // revelan si la tasa es variable y sirven de tasa de reserva cuando el contenedor no declara ninguna.
    private static (Rational? Measured, bool Irregular) ScanTimestamps(AVFormatContext* format, int index)
    {
        AVStream* stream = format->streams[index];
        var timestamps = new List<long>(VfrScanPackets);
        AVPacket* packet = ffmpeg.av_packet_alloc();
        try
        {
            int read = 0;
            while (timestamps.Count < VfrScanPackets && read < VfrScanPackets * 8 && ffmpeg.av_read_frame(format, packet) >= 0)
            {
                read++;
                if (packet->stream_index == index && packet->pts != ffmpeg.AV_NOPTS_VALUE)
                {
                    timestamps.Add(packet->pts);
                }

                ffmpeg.av_packet_unref(packet);
            }
        }
        finally
        {
            ffmpeg.av_packet_free(&packet);
            ffmpeg.av_seek_frame(format, index, long.MinValue, ffmpeg.AVSEEK_FLAG_BACKWARD);
        }

        if (timestamps.Count < 3)
        {
            return (null, false);
        }

        timestamps.Sort();
        // Los paquetes llegan en orden de decodificación: con B-frames, los últimos leídos pueden dejar
        // huecos en el orden de presentación que no son de la tasa.
        if (timestamps.Count > 32)
        {
            timestamps.RemoveRange(timestamps.Count - 16, 16);
        }

        var deltas = new List<long>(timestamps.Count - 1);
        for (int i = 1; i < timestamps.Count; i++)
        {
            deltas.Add(timestamps[i] - timestamps[i - 1]);
        }

        deltas.Sort();
        long median = deltas[deltas.Count / 2];
        if (median <= 0)
        {
            return (null, false);
        }

        // Una marca redondeada a la base de tiempo puede bailar ±1 unidad sin que la tasa cambie.
        long tolerance = Math.Max(1, median / 10);
        bool irregular = deltas.Exists(d => Math.Abs(d - median) > tolerance);
        Rational? measured = Av.IsValid(stream->time_base)
            ? SnapFrameRate(new Rational(stream->time_base.den, median * stream->time_base.num))
            : null;
        return (measured, irregular);
    }
}
