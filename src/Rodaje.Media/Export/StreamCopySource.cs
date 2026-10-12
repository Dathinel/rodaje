using System.Globalization;
using FFmpeg.AutoGen.Abstractions;
using Rodaje.Core.Timing;
using Rodaje.Media.Interop;
using Rodaje.Media.Probe;

namespace Rodaje.Media.Export;

/// <summary>Si un tramo de un archivo se puede copiar sin recodificar y desde dónde lo busca <c>ffmpeg.exe</c>.</summary>
/// <param name="CanCopy">Empieza en un keyframe de GOP cerrado y termina en otro (o al final del archivo), y no es HDR.</param>
/// <param name="Reason">Por qué no, en español, o <c>null</c>.</param>
/// <param name="SeekSeconds">Valor de <c>-ss</c> antes de <c>-i</c>: el keyframe de inicio en la línea de tiempo del archivo.</param>
/// <param name="StreamIndex">Flujo de video que se copia.</param>
public sealed record StreamCopyCheck(bool CanCopy, string? Reason, double SeekSeconds, int StreamIndex)
{
    public static StreamCopyCheck No(string reason) => new(false, reason, 0, -1);
}

/// <summary>
/// Comprueba en el archivo lo que la copia directa necesita: cortes en keyframes, sin cuadros que dependan del GOP
/// anterior (GOP abierto, los RASL de HEVC) en ninguno de los dos cortes, y sin HDR (la exportación es SDR BT.709).
/// </summary>
public static unsafe class StreamCopySource
{
    // Paquetes que se miran tras cada keyframe: los cuadros iniciales de un GOP abierto llegan enseguida.
    private const int LeadingWindow = 64;

    /// <param name="path">Archivo de origen.</param>
    /// <param name="firstFrame">Primer cuadro del tramo, con la numeración de <c>VideoDecoder</c>.</param>
    /// <param name="frameCount">Cuadros del tramo.</param>
    /// <param name="cancellationToken">Cancela entre paquetes.</param>
    public static StreamCopyCheck Inspect(string path, long firstFrame, long frameCount, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfNegative(firstFrame);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameCount);
        KeyframeIndex keys = KeyframeIndex.Read(path, cancellationToken: cancellationToken);
        long end = firstFrame + frameCount;
        if (keys.AtOrBefore(firstFrame) != firstFrame)
        {
            return StreamCopyCheck.No(string.Create(CultureInfo.InvariantCulture,
                $"el tramo empieza en el cuadro {firstFrame}, que no es un keyframe (el anterior es el {keys.AtOrBefore(firstFrame) ?? 0} y el siguiente el {keys.After(firstFrame)})"));
        }

        bool toEnd = end >= keys.FrameCount;
        if (!toEnd && keys.AtOrBefore(end) != end)
        {
            return StreamCopyCheck.No(string.Create(CultureInfo.InvariantCulture,
                $"el tramo termina en el cuadro {end}, que no es un keyframe ni el final del archivo"));
        }

        AVFormatContext* format = Av.OpenInput(path);
        AVPacket* packet = ffmpeg.av_packet_alloc();
        try
        {
            MediaInfo info = MediaProbe.Probe(format, path, scanTimestamps: false);
            VideoStreamInfo video = info.Video!;
            AVStream* stream = format->streams[video.StreamIndex];
            AVColorTransferCharacteristic transfer = stream->codecpar->color_trc;
            if (transfer is AVColorTransferCharacteristic.AVCOL_TRC_SMPTE2084 or AVColorTransferCharacteristic.AVCOL_TRC_ARIB_STD_B67)
            {
                return StreamCopyCheck.No("el original es HDR y la exportación es SDR");
            }

            for (int i = 0; i < format->nb_streams; i++)
            {
                format->streams[i]->discard = i == video.StreamIndex ? AVDiscard.AVDISCARD_DEFAULT : AVDiscard.AVDISCARD_ALL;
            }

            AVRational timeBase = stream->time_base;
            long Pts(long frame) => Av.FromTime(video.StartTime + Time.FromFrames(frame, video.FrameRate), timeBase);
            long half = Math.Max(1, Av.FromTime(Time.FromFrames(1, video.FrameRate), timeBase) / 2);
            if (HasLeadingPictures(format, video.StreamIndex, packet, Pts(firstFrame), half, cancellationToken))
            {
                return StreamCopyCheck.No("el corte de inicio cae en un GOP abierto (hay cuadros que dependen del GOP anterior)");
            }

            if (!toEnd && HasLeadingPictures(format, video.StreamIndex, packet, Pts(end), half, cancellationToken))
            {
                return StreamCopyCheck.No("el corte final cae en un GOP abierto");
            }

            // «-ss» antes de «-i» cuenta desde el inicio del archivo; un microsegundo de más para no caer en el keyframe anterior.
            double fileStart = format->start_time == ffmpeg.AV_NOPTS_VALUE ? 0 : format->start_time / (double)ffmpeg.AV_TIME_BASE;
            double seconds = (video.StartTime + Time.FromFrames(firstFrame, video.FrameRate)).ToSeconds() - fileStart;
            return new StreamCopyCheck(true, null, Math.Ceiling(seconds * 1e6) / 1e6 + 1e-6, video.StreamIndex);
        }
        finally
        {
            ffmpeg.av_packet_free(&packet);
            ffmpeg.avformat_close_input(&format);
        }
    }

    // Tras el keyframe, en orden de decodificación y hasta el siguiente, ¿algún cuadro se presenta antes que él? Esos son
    // los iniciales de un GOP abierto, que necesitan el GOP anterior. Sin encontrar el keyframe se responde que sí.
    private static bool HasLeadingPictures(AVFormatContext* format, int index, AVPacket* packet, long keyPts, long half,
        CancellationToken cancellationToken)
    {
        if (ffmpeg.av_seek_frame(format, index, keyPts, ffmpeg.AVSEEK_FLAG_BACKWARD) < 0)
        {
            return true;
        }

        long? key = null;
        int after = 0;
        while (ffmpeg.av_read_frame(format, packet) >= 0)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (packet->stream_index != index || packet->pts == ffmpeg.AV_NOPTS_VALUE)
                {
                    continue;
                }

                bool isKey = (packet->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0;
                if (key is null)
                {
                    if (isKey && Math.Abs(packet->pts - keyPts) <= half)
                    {
                        key = packet->pts;
                    }
                    else if (packet->pts > keyPts + half && isKey)
                    {
                        return true;
                    }

                    continue;
                }

                if (isKey || ++after > LeadingWindow)
                {
                    return false;
                }

                if (packet->pts < key.Value - half)
                {
                    return true;
                }
            }
            finally
            {
                ffmpeg.av_packet_unref(packet);
            }
        }

        return key is null;
    }
}
