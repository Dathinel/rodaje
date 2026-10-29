using System.Runtime.InteropServices;
using FFmpeg.AutoGen.Abstractions;
using Rodaje.Core.Timing;
using Rodaje.Media.Interop;
using Rodaje.Media.Video;

namespace Rodaje.Media.Probe;

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
        Rational? avg = Sane(average);
        Rational? r = Sane(real);
        if (avg is { } a && r is { } b)
        {
            return Math.Abs(a.ToDouble() - b.ToDouble()) / b.ToDouble() < 0.001 ? b : a;
        }

        return avg ?? r;
    }

    /// <summary>
    /// Tasa con la que se numeran los cuadros: la declarada (<see cref="ChooseFrameRate"/>) o, si no hay o la tasa
    /// es variable, la medida en las marcas. La medida pasa por las mismas reglas de cordura que la declarada.
    /// </summary>
    public static Rational? ResolveFrameRate(Rational? average, Rational? real, Rational? measured, bool variable)
    {
        Rational? chosen = ChooseFrameRate(average, real);
        // Con tasa variable el promedio (29,87…) numeraría mal; el intervalo más común es la tasa nominal.
        return Sane(measured) is { } m && (chosen is null || variable) ? m : chosen;
    }

    private static Rational? Sane(Rational? rate) => rate is { } v && v.ToDouble() is >= 1 and <= 1000 ? SnapFrameRate(v) : null;

    internal static MediaInfo Probe(AVFormatContext* format, string path, bool scanTimestamps)
    {
        try
        {
            return ProbeStreams(format, path, scanTimestamps);
        }
        catch (ArithmeticException ex)
        {
            // Un contenedor dañado puede declarar duraciones, inicios o cuadros que no caben en Time: las cuentas comprobadas
            // lanzaban OverflowException, que quien lee solo espera como MediaException (no se entendía o cerraba la app).
            throw new MediaException($"El archivo {path} declara una duración o unas marcas de tiempo imposibles; puede estar dañado.", ex);
        }
    }

    private static MediaInfo ProbeStreams(AVFormatContext* format, string path, bool scanTimestamps)
    {
        int videoIndex = ffmpeg.av_find_best_stream(format, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
        int audioIndex = ffmpeg.av_find_best_stream(format, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, null, 0);
        if (videoIndex >= 0 && (format->streams[videoIndex]->disposition & ffmpeg.AV_DISPOSITION_ATTACHED_PIC) != 0)
        {
            videoIndex = -1;
        }

        if (videoIndex < 0 && audioIndex < 0)
        {
            throw new MediaException($"El archivo {path} no tiene flujos de video ni de audio que Rodaje pueda leer.");
        }

        AVRational microseconds = new() { num = 1, den = ffmpeg.AV_TIME_BASE };
        Time containerStart = format->start_time == ffmpeg.AV_NOPTS_VALUE ? Time.Zero : Av.ToTime(format->start_time, microseconds);
        Time? containerDuration = KnownDuration(format->duration, microseconds);

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

        Dictionary<string, string> tags = MediaTags.Read(format->metadata);
        if (videoIndex >= 0)
        {
            MediaTags.Read(format->streams[videoIndex]->metadata, tags);
        }

        string container = Marshal.PtrToStringUTF8((nint)format->iformat->name) ?? "";
        if (video is { FrameCount: <= 1 } image && audio is null && MediaSupport.IsStillImageFormat(container, image.Codec))
        {
            int rotation = ReadImageRotation(format, videoIndex) ?? image.Rotation;
            int width = (int)Math.Round(image.Width * image.SampleAspectRatio.ToDouble());
            video = image with
            {
                FrameCount = 1, Duration = Time.FromFrames(1, image.FrameRate), Rotation = rotation,
                DisplayWidth = rotation is 90 or 270 ? image.Height : width,
                DisplayHeight = rotation is 90 or 270 ? width : image.Height,
            };
            duration = Time.Zero;
        }

        return new MediaInfo
        {
            Tags = MediaTags.Parse(tags),
            Path = path,
            ContainerFormat = container,
            Duration = duration,
            ContainerStartTime = containerStart,
            Video = video,
            Audio = audio,
        };
    }

    /// <summary>
    /// Los cuadros que declara el contenedor, sin pasar de los que caben en <see cref="Time"/> a esa tasa: un archivo dañado
    /// puede declarar billones y <see cref="Time.FromFrames"/> lanzaba <see cref="OverflowException"/>.
    /// </summary>
    internal static long SafeFrameCount(long frames, Rational rate)
    {
        Int128 max = (Int128)long.MaxValue * rate.Num / ((Int128)Time.TicksPerSecond * rate.Den) - 1;
        return frames <= 0 ? frames : (long)Int128.Min(frames, Int128.Max(max, 0));
    }

    private static Time StreamStart(AVStream* stream, Time containerStart) =>
        stream->start_time == ffmpeg.AV_NOPTS_VALUE ? containerStart : Av.ToTime(stream->start_time, stream->time_base);

    private static Time? StreamDuration(AVStream* stream, Time start, Time containerStart, Time? containerDuration)
    {
        return KnownDuration(stream->duration, stream->time_base) ?? RemainingContainerDuration(containerDuration, start, containerStart);
    }

    /// <summary>
    /// La duración que declara una cabecera, o <see langword="null"/> (desconocida) si no es positiva, no hay marca o no cabe
    /// en <see cref="Time"/>: una cabecera dañada con una duración enorme se truncaba a una duración inventada.
    /// </summary>
    internal static Time? KnownDuration(long duration, AVRational timeBase) =>
        duration > 0 && Av.TryToTime(duration, timeBase, out Time time) ? time : null;

    /// <summary>
    /// Lo que queda del contenedor desde que empieza el flujo, para un flujo que no declara su duración. Un flujo que dice
    /// empezar en el final del contenedor o después (archivo dañado) daba una duración nula o negativa, y con ella la del
    /// medio entero: se toma como desconocida (<see langword="null"/>).
    /// </summary>
    internal static Time? RemainingContainerDuration(Time? containerDuration, Time start, Time containerStart) =>
        containerDuration is { } d && d - (start - containerStart) is { Ticks: > 0 } rest ? rest : null;

    private static VideoStreamInfo ReadVideo(AVFormatContext* format, int index, Time containerStart, Time? containerDuration, bool scan)
    {
        AVStream* stream = format->streams[index];
        AVCodecParameters* par = stream->codecpar;
        var pixelFormat = (AVPixelFormat)par->format;
        AVPixFmtDescriptor* descriptor = ffmpeg.av_pix_fmt_desc_get(pixelFormat);
        int bitDepth = descriptor != null ? descriptor->comp[0].depth : 8;

        Rational? average = ToRational(stream->avg_frame_rate);
        Rational? real = ToRational(stream->r_frame_rate);
        bool vfr = average is { } a && real is { } r && Math.Abs(a.ToDouble() - r.ToDouble()) / r.ToDouble() > 0.02;
        Rational? measured = null;
        if (scan)
        {
            (measured, bool irregular) = ScanTimestamps(format, index);
            vfr |= irregular;
        }

        Rational rate = ResolveFrameRate(average, real, measured, vfr) ?? Rational.Fps30;
        Time start = StreamStart(stream, containerStart);
        Time? streamDuration = StreamDuration(stream, start, containerStart, containerDuration);
        long frameCount = SafeFrameCount(stream->nb_frames > 0
            ? stream->nb_frames
            : streamDuration is { } sd ? Math.Max(1, (long)Av.RoundDiv((Int128)sd.Ticks * rate.Num, (Int128)Time.TicksPerSecond * rate.Den)) : 0, rate);
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
            Color = VideoColor.Resolve(par->color_space, par->color_range, pixelFormat, par->height)
                .WithTransfer(par->color_trc, par->color_primaries),
            HdrTransfer = par->color_trc switch
            {
                AVColorTransferCharacteristic.AVCOL_TRC_SMPTE2084 => "PQ",
                AVColorTransferCharacteristic.AVCOL_TRC_ARIB_STD_B67 => "HLG",
                _ => null,
            },
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

    private static int? ReadImageRotation(AVFormatContext* format, int index)
    {
        AVCodecParameters* par = format->streams[index]->codecpar;
        AVCodec* decoder = ffmpeg.avcodec_find_decoder(par->codec_id);
        if (decoder == null)
        {
            return null;
        }

        AVCodecContext* codec = ffmpeg.avcodec_alloc_context3(decoder);
        AVPacket* packet = ffmpeg.av_packet_alloc();
        AVFrame* frame = ffmpeg.av_frame_alloc();
        try
        {
            Av.Check(ffmpeg.avcodec_parameters_to_context(codec, par), "avcodec_parameters_to_context");
            codec->thread_count = 1;
            Av.Check(ffmpeg.avcodec_open2(codec, decoder, null), "avcodec_open2");
            while (ffmpeg.av_read_frame(format, packet) >= 0)
            {
                if (packet->stream_index == index)
                {
                    Av.Check(ffmpeg.avcodec_send_packet(codec, packet), "avcodec_send_packet");
                    if (ffmpeg.avcodec_receive_frame(codec, frame) >= 0)
                    {
                        AVFrameSideData* matrix = ffmpeg.av_frame_get_side_data(frame, AVFrameSideDataType.AV_FRAME_DATA_DISPLAYMATRIX);
                        if (matrix != null && matrix->size >= 9 * sizeof(int))
                        {
                            double angle = ffmpeg.av_display_rotation_get(*(int9*)matrix->data);
                            return double.IsNaN(angle) ? null : ((int)Math.Round(-angle / 90) * 90 % 360 + 360) % 360;
                        }

                        return null;
                    }
                }

                ffmpeg.av_packet_unref(packet);
            }

            return null;
        }
        finally
        {
            ffmpeg.av_frame_free(&frame);
            ffmpeg.av_packet_free(&packet);
            ffmpeg.avcodec_free_context(&codec);
            RewindToStart(format, index);
        }
    }

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

    // Vuelve al primer paquete del flujo. av_seek_frame(long.MinValue) no mueve la posición en MP4, así que se busca
    // una marca concreta con avformat_seek_file. La marca de inicio del flujo (start_time) es el pts del primer cuadro
    // y en algunos contenedores (MPEG-TS con cuadros B) la búsqueda compara contra el dts, que va antes: por eso se
    // usa el menor entre start_time y el primer dts de video leído (firstDts, si se conoce).
    internal static void RewindToStart(AVFormatContext* format, int index, long? firstDts = null)
    {
        AVStream* stream = format->streams[index];
        long start = stream->start_time == ffmpeg.AV_NOPTS_VALUE ? 0 : stream->start_time;
        if (firstDts is { } dts)
        {
            start = Math.Min(start, dts);
        }

        if (ffmpeg.avformat_seek_file(format, index, long.MinValue, start, start, 0) < 0)
        {
            ffmpeg.av_seek_frame(format, index, start, ffmpeg.AVSEEK_FLAG_BACKWARD);
        }
    }

    // Lee (sin decodificar) los primeros paquetes del flujo: los intervalos entre marcas de presentación
    // revelan si la tasa es variable y sirven de tasa de reserva cuando el contenedor no declara ninguna.
    private static (Rational? Measured, bool Irregular) ScanTimestamps(AVFormatContext* format, int index)
    {
        AVStream* stream = format->streams[index];
        var timestamps = new List<long>(VfrScanPackets);
        AVPacket* packet = ffmpeg.av_packet_alloc();
        long? firstDts = null;
        try
        {
            int read = 0;
            while (timestamps.Count < VfrScanPackets && read < VfrScanPackets * 8 && ffmpeg.av_read_frame(format, packet) >= 0)
            {
                read++;
                if (packet->stream_index == index && packet->dts != ffmpeg.AV_NOPTS_VALUE
                    && (firstDts is null || packet->dts < firstDts))
                {
                    firstDts = packet->dts;
                }

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
            RewindToStart(format, index, firstDts);
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
