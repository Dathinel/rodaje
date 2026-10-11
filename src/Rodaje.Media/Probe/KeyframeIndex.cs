using System.Runtime.InteropServices;
using FFmpeg.AutoGen.Abstractions;
using Rodaje.Core.Timing;
using Rodaje.Media.Interop;

namespace Rodaje.Media.Probe;

/// <summary>De dónde salió un <see cref="KeyframeIndex"/>.</summary>
public enum KeyframeIndexSource
{
    /// <summary>Del índice del contenedor (MP4/MOV), leyendo solo el paquete de cada keyframe para su marca de presentación.</summary>
    ContainerIndex,

    /// <summary>Recorriendo todos los paquetes de video sin decodificarlos.</summary>
    PacketScan,
}

/// <summary>
/// Keyframes del flujo de video principal, como índices de cuadro con la misma numeración que
/// <see cref="Video.VideoDecoder"/> (<c>start_time + n / tasa</c>, en orden de presentación).
/// </summary>
/// <remarks>
/// Sirve para la reversa (cada ventana va de un keyframe al cuadro pedido) y para mostrar el keyframe cercano de 16× a
/// 100×. Decodificar desde uno de estos cuadros nunca necesita lo anterior, salvo los cuadros iniciales de un GOP abierto,
/// que tienen índices menores que el keyframe y se obtienen desde el keyframe anterior.
/// </remarks>
public sealed unsafe class KeyframeIndex
{
    private readonly long[] _frames;

    internal KeyframeIndex(IEnumerable<long> frames, Rational frameRate, long frameCount, KeyframeIndexSource source)
    {
        _frames = frames.Where(f => f >= 0).Distinct().Order().ToArray();
        FrameRate = frameRate;
        FrameCount = Math.Max(frameCount, _frames.Length > 0 ? _frames[^1] + 1 : 0);
        Source = source;
    }

    /// <summary>Índices de los keyframes, de menor a mayor.</summary>
    public IReadOnlyList<long> Frames => _frames;

    public Rational FrameRate { get; }

    /// <summary>Cuadros del medio según el contenedor (al menos uno más que el último keyframe).</summary>
    public long FrameCount { get; }

    public KeyframeIndexSource Source { get; }

    /// <summary>Mayor distancia entre keyframes, contando el tramo final hasta <see cref="FrameCount"/>.</summary>
    public long MaxInterval
    {
        get
        {
            if (_frames.Length == 0)
            {
                return FrameCount;
            }

            long max = FrameCount - _frames[^1];
            for (int i = 1; i < _frames.Length; i++)
            {
                max = Math.Max(max, _frames[i] - _frames[i - 1]);
            }

            return max;
        }
    }

    /// <summary>El último keyframe que no pasa de <paramref name="frame"/>; <c>null</c> si no hay ninguno antes.</summary>
    public long? AtOrBefore(long frame)
    {
        int i = Array.BinarySearch(_frames, frame);
        i = i >= 0 ? i : ~i - 1;
        return i >= 0 ? _frames[i] : null;
    }

    /// <summary>El primer keyframe posterior a <paramref name="frame"/>; <c>null</c> si no hay más.</summary>
    public long? After(long frame)
    {
        int i = Array.BinarySearch(_frames, frame);
        i = i >= 0 ? i + 1 : ~i;
        return i < _frames.Length ? _frames[i] : null;
    }

    /// <summary>
    /// Tramo que se decodifica de una vez para llegar a <paramref name="frame"/>: desde su keyframe (o el primero si va
    /// antes) hasta el siguiente keyframe, o hasta <see cref="FrameCount"/>. El final no se incluye.
    /// </summary>
    public (long Start, long End) GopOf(long frame)
    {
        long start = AtOrBefore(frame) ?? (_frames.Length > 0 ? _frames[0] : 0);
        return (start, After(start) ?? Math.Max(FrameCount, start + 1));
    }

    /// <summary>Lee los keyframes del video principal sin decodificar.</summary>
    /// <param name="path">Archivo de medio.</param>
    /// <param name="useContainerIndex">
    /// Si el contenedor trae un índice completo (MP4/MOV), lo usa y solo lee un paquete por keyframe; si no, recorre
    /// todos los paquetes de video.
    /// </param>
    /// <param name="cancellationToken">Cancela entre paquetes.</param>
    /// <exception cref="FfmpegNotFoundException">No se pudo cargar FFmpeg.</exception>
    /// <exception cref="MediaException">El archivo no se pudo abrir o no tiene video.</exception>
    public static KeyframeIndex Read(string path, bool useContainerIndex = true, CancellationToken cancellationToken = default)
    {
        AVFormatContext* format = Av.OpenInput(path);
        AVPacket* packet = ffmpeg.av_packet_alloc();
        try
        {
            MediaInfo info = MediaProbe.Probe(format, path, scanTimestamps: true);
            VideoStreamInfo video = info.Video ?? throw new MediaException($"El archivo {path} no tiene un flujo de video.");
            int index = video.StreamIndex;
            DiscardOthers(format, index);
            AVStream* stream = format->streams[index];
            var clock = new SlotClock(Av.FromTime(video.StartTime, stream->time_base), stream->time_base, video.FrameRate);
            List<long>? frames = useContainerIndex ? FromContainerIndex(format, stream, index, packet, clock, video.FrameCount, cancellationToken) : null;
            KeyframeIndexSource source = KeyframeIndexSource.ContainerIndex;
            long count = video.FrameCount;
            if (frames is null)
            {
                // Se vuelve a abrir: tras el análisis o las búsquedas, volver al principio con un seek no es fiable en
                // todos los contenedores (en MP4 la búsqueda a long.MinValue no se mueve).
                ffmpeg.avformat_close_input(&format);
                format = Av.OpenInput(path);
                DiscardOthers(format, index);
                (frames, long packets) = Scan(format, index, packet, clock, cancellationToken);
                source = KeyframeIndexSource.PacketScan;
                count = Math.Max(count, packets);
            }

            return new KeyframeIndex(frames, video.FrameRate, count, source);
        }
        finally
        {
            ffmpeg.av_packet_free(&packet);
            ffmpeg.avformat_close_input(&format);
        }
    }

    // El índice de MP4 va en DTS, que con cuadros B no es la marca de presentación, y la búsqueda de mov compara con otra
    // marca según la versión: se busca cada keyframe, se lee su paquete y solo vale si su DTS es el del índice. El índice
    // tiene que traer todas las muestras (MP4/MOV sí; Matroska solo trae los Cues).
    private static List<long>? FromContainerIndex(AVFormatContext* format, AVStream* stream, int index, AVPacket* packet,
        SlotClock clock, long frameCount, CancellationToken cancellationToken)
    {
        string name = Marshal.PtrToStringUTF8((nint)format->iformat->name) ?? "";
        int entries = ffmpeg.avformat_index_get_entries_count(stream);
        if (!name.Contains("mp4", StringComparison.Ordinal) || entries == 0 || entries < frameCount)
        {
            return null;
        }

        var timestamps = new List<long>();
        for (int i = 0; i < entries; i++)
        {
            AVIndexEntry* entry = ffmpeg.avformat_index_get_entry(stream, i);
            if (entry != null && (entry->flags2_size30 & ffmpeg.AVINDEX_KEYFRAME) != 0)
            {
                timestamps.Add(entry->timestamp);
            }
        }

        // Con keyframes muy seguidos (intra) una búsqueda por keyframe sale más cara que leer todo.
        if (timestamps.Count * 8L > entries)
        {
            return null;
        }

        var frames = new List<long>(timestamps.Count);
        foreach (long dts in timestamps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryReadKeyAt(format, index, packet, dts, 0, out long pts) && !TryReadKeyAt(format, index, packet, dts, ffmpeg.AVSEEK_FLAG_BACKWARD, out pts))
            {
                return null;
            }

            frames.Add(clock.Slot(pts));
        }

        return frames;
    }

    private static void DiscardOthers(AVFormatContext* format, int index)
    {
        for (int i = 0; i < format->nb_streams; i++)
        {
            format->streams[i]->discard = i == index ? AVDiscard.AVDISCARD_DEFAULT : AVDiscard.AVDISCARD_ALL;
        }
    }

    private static bool TryReadKeyAt(AVFormatContext* format, int index, AVPacket* packet, long dts, int flags, out long pts)
    {
        pts = 0;
        if (ffmpeg.av_seek_frame(format, index, dts, flags) < 0)
        {
            return false;
        }

        while (ffmpeg.av_read_frame(format, packet) >= 0)
        {
            bool match = packet->stream_index == index && packet->dts == dts && (packet->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0;
            pts = packet->pts != ffmpeg.AV_NOPTS_VALUE ? packet->pts : packet->dts;
            bool mine = packet->stream_index == index;
            ffmpeg.av_packet_unref(packet);
            if (mine)
            {
                return match;
            }
        }

        return false;
    }

    private static (List<long> Frames, long Packets) Scan(AVFormatContext* format, int index, AVPacket* packet, SlotClock clock,
        CancellationToken cancellationToken)
    {
        var frames = new List<long>();
        long packets = 0;
        int errors = 0;
        while (true)
        {
            if ((packets & 255) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            int r = ffmpeg.av_read_frame(format, packet);
            if (r == ffmpeg.AVERROR_EOF)
            {
                break;
            }

            if (r < 0)
            {
                if (++errors > 64)
                {
                    break;
                }

                continue;
            }

            errors = 0;
            if (packet->stream_index == index)
            {
                packets++;
                long ts = packet->pts != ffmpeg.AV_NOPTS_VALUE ? packet->pts : packet->dts;
                if ((packet->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0 && ts != ffmpeg.AV_NOPTS_VALUE)
                {
                    frames.Add(clock.Slot(ts));
                }
            }

            ffmpeg.av_packet_unref(packet);
        }

        return (frames, packets);
    }

    /// <summary>Marca de tiempo → índice de cuadro, con el mismo redondeo que el decodificador.</summary>
    private readonly record struct SlotClock(long OriginTs, AVRational TimeBase, Rational Rate)
    {
        public long Slot(long timestamp) =>
            (long)Av.RoundDiv((Int128)(timestamp - OriginTs) * TimeBase.num * Rate.Num, (Int128)TimeBase.den * Rate.Den);
    }
}
