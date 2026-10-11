using FFmpeg.AutoGen.Abstractions;
using Rodaje.Media.Interop;
using Rodaje.Media.Probe;
using Rodaje.Media.Tests.Support;
using Rodaje.TestMedia;

namespace Rodaje.Media.Tests;

// Tras el análisis de marcas de tiempo, quien lea paquetes sin hacer un seek propio debe empezar en el
// primer paquete del archivo, también en MP4 (donde av_seek_frame(long.MinValue) no se movía).
public sealed unsafe class ProbeRewindTests
{
    // Los remux sin recodificar (ts, mov) conservan los cuadros B del H.264 original; en MPEG-TS el dts va antes que el pts.
    public static TheoryData<string> Clips => new(TestClips.H264.Name, TestClips.Hevc.Name, TestClips.H264Mkv.Name, "remux:ts", "remux:mov");

    private static string PathFor(string clip) =>
        clip.StartsWith("remux:", StringComparison.Ordinal) ? TestClips.Remuxed(clip["remux:".Length..]) : TestClips.PathOf(DecodeModes.Clip(clip));

    [FfmpegLibraryTheory]
    [MemberData(nameof(Clips))]
    public void The_first_packet_after_the_scan_is_the_first_of_the_file(string clip)
    {
        string path = PathFor(clip);
        (int stream, long dts, long pos, int count) expected = FirstPacket(path, scan: false);
        (int stream, long dts, long pos, int count) actual = FirstPacket(path, scan: true);

        Assert.Equal(expected, actual);
    }

    private static (int Stream, long Dts, long Pos, int VideoPackets) FirstPacket(string path, bool scan)
    {
        AVFormatContext* format = Av.OpenInput(path);
        AVPacket* packet = ffmpeg.av_packet_alloc();
        try
        {
            if (scan)
            {
                MediaProbe.Probe(format, path, scanTimestamps: true);
            }

            // El rebobinado busca el inicio del flujo de video: un paquete de audio anterior en el archivo no cuenta.
            int video = MediaProbe.Probe(format, path, scanTimestamps: false).Video!.StreamIndex;
            (int Stream, long Dts, long Pos) first = (-1, 0, 0);
            int count = 0;
            while (ffmpeg.av_read_frame(format, packet) >= 0)
            {
                if (packet->stream_index == video)
                {
                    if (count++ == 0)
                    {
                        first = (packet->stream_index, packet->dts, packet->pos);
                    }
                }

                ffmpeg.av_packet_unref(packet);
            }

            Assert.True(count > 0, "No hay paquetes de video.");
            return (first.Stream, first.Dts, first.Pos, count);
        }
        finally
        {
            ffmpeg.av_packet_free(&packet);
            ffmpeg.avformat_close_input(&format);
        }
    }
}
