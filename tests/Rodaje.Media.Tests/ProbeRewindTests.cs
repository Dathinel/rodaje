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
    public static TheoryData<string> Clips => new(TestClips.H264.Name, TestClips.Hevc.Name, TestClips.H264Mkv.Name);

    [FfmpegLibraryTheory]
    [MemberData(nameof(Clips))]
    public void The_first_packet_after_the_scan_is_the_first_of_the_file(string clip)
    {
        string path = TestClips.PathOf(DecodeModes.Clip(clip));
        (int stream, long dts, long pos) expected = FirstPacket(path, scan: false);
        (int stream, long dts, long pos) actual = FirstPacket(path, scan: true);

        Assert.Equal(expected, actual);
    }

    private static (int Stream, long Dts, long Pos) FirstPacket(string path, bool scan)
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
            for (int i = 0; i < 1000; i++)
            {
                Assert.True(ffmpeg.av_read_frame(format, packet) >= 0);
                if (packet->stream_index == video)
                {
                    return (packet->stream_index, packet->dts, packet->pos);
                }

                ffmpeg.av_packet_unref(packet);
            }

            throw new Xunit.Sdk.XunitException("No hay paquetes de video.");
        }
        finally
        {
            ffmpeg.av_packet_free(&packet);
            ffmpeg.avformat_close_input(&format);
        }
    }
}
