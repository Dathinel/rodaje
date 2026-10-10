using FFmpeg.AutoGen.Abstractions;
using Rodaje.Media.Interop;

namespace Rodaje.Media.Proxies;

/// <summary>Estructura del flujo de video medida leyendo paquetes, sin decodificar.</summary>
/// <param name="Bitrate">Bits por segundo del video (declarados, o calculados de los paquetes leídos).</param>
/// <param name="MaxKeyframeInterval">
/// Mayor distancia entre keyframes en los paquetes leídos. Si solo hubo un keyframe, es la cantidad de paquetes
/// leídos desde él (una cota inferior, ver <see cref="IntervalIsLowerBound"/>).
/// </param>
/// <param name="KeyframeCount">Keyframes vistos.</param>
/// <param name="PacketsScanned">Paquetes de video leídos.</param>
/// <param name="IntervalIsLowerBound">La lectura terminó antes de ver un segundo keyframe.</param>
public sealed record StreamStructure(long? Bitrate, int? MaxKeyframeInterval, int KeyframeCount, int PacketsScanned, bool IntervalIsLowerBound);

/// <summary>Mide la distancia entre keyframes y la tasa de bits de un video leyendo sus primeros paquetes.</summary>
public static unsafe class StreamStructureProbe
{
    /// <summary>Lee hasta <paramref name="maxPackets"/> paquetes del flujo de video principal.</summary>
    /// <exception cref="FfmpegNotFoundException">No se pudo cargar FFmpeg.</exception>
    /// <exception cref="MediaException">El archivo no se pudo abrir o no tiene video.</exception>
    public static StreamStructure Probe(string path, int maxPackets = 900)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPackets, 2);
        AVFormatContext* format = Av.OpenInput(path);
        AVPacket* packet = ffmpeg.av_packet_alloc();
        try
        {
            int index = ffmpeg.av_find_best_stream(format, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
            if (index < 0 || (format->streams[index]->disposition & ffmpeg.AV_DISPOSITION_ATTACHED_PIC) != 0)
            {
                throw new MediaException($"El archivo {path} no tiene un flujo de video.");
            }

            AVStream* stream = format->streams[index];
            var keyPositions = new List<int>();
            int packets = 0;
            long bytes = 0;
            long firstTs = long.MaxValue;
            long lastTs = long.MinValue;
            int reads = 0;
            while (packets < maxPackets && reads++ < maxPackets * 16 && ffmpeg.av_read_frame(format, packet) >= 0)
            {
                if (packet->stream_index == index)
                {
                    if ((packet->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0)
                    {
                        keyPositions.Add(packets);
                    }

                    bytes += packet->size;
                    long ts = packet->pts != ffmpeg.AV_NOPTS_VALUE ? packet->pts : packet->dts;
                    if (ts != ffmpeg.AV_NOPTS_VALUE)
                    {
                        firstTs = Math.Min(firstTs, ts);
                        lastTs = Math.Max(lastTs, ts + Math.Max(0, packet->duration));
                    }

                    packets++;
                }

                ffmpeg.av_packet_unref(packet);
            }

            return Summarize(keyPositions, packets, DeclaredBitrate(format, stream) ?? MeasuredBitrate(bytes, firstTs, lastTs, stream->time_base));
        }
        finally
        {
            ffmpeg.av_packet_free(&packet);
            ffmpeg.avformat_close_input(&format);
        }
    }

    /// <summary>Resume las posiciones (en orden de decodificación) de los paquetes clave.</summary>
    internal static StreamStructure Summarize(IReadOnlyList<int> keyPositions, int packets, long? bitrate)
    {
        if (keyPositions.Count == 0)
        {
            return new StreamStructure(bitrate, packets > 0 ? packets : null, 0, packets, IntervalIsLowerBound: packets > 0);
        }

        int max = 0;
        for (int i = 1; i < keyPositions.Count; i++)
        {
            max = Math.Max(max, keyPositions[i] - keyPositions[i - 1]);
        }

        // El tramo final sin keyframe también cuenta si ya es más largo que lo visto: es una cota inferior.
        int tail = packets - keyPositions[^1];
        bool lowerBound = keyPositions.Count == 1 || tail > max;
        return new StreamStructure(bitrate, Math.Max(max, tail), keyPositions.Count, packets, lowerBound);
    }

    private static long? DeclaredBitrate(AVFormatContext* format, AVStream* stream)
    {
        if (stream->codecpar->bit_rate > 0)
        {
            return stream->codecpar->bit_rate;
        }

        return format->nb_streams == 1 && format->bit_rate > 0 ? format->bit_rate : null;
    }

    private static long? MeasuredBitrate(long bytes, long firstTs, long lastTs, AVRational timeBase)
    {
        if (bytes == 0 || lastTs <= firstTs || !Av.IsValid(timeBase))
        {
            return null;
        }

        double seconds = (double)(lastTs - firstTs) * timeBase.num / timeBase.den;
        return seconds > 0 ? (long)(bytes * 8 / seconds) : null;
    }
}
