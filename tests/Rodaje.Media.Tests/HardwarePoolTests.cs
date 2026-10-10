using FFmpeg.AutoGen.Abstractions;
using Rodaje.Core.Timing;
using Rodaje.Media.Gpu;
using Rodaje.Media.Tests.Support;
using Rodaje.Media.Video;
using Rodaje.Playback;
using Rodaje.TestMedia;
using Xunit.Abstractions;

namespace Rodaje.Media.Tests;

/// <summary>Necesita D3D11VA y un codificador HEVC para generar el clip de 4K.</summary>
internal sealed class HevcHardwareFactAttribute : FactAttribute
{
    public HevcHardwareFactAttribute() =>
        Skip = MediaTestEnvironment.HardwareSkipReason ?? new Rodaje.TestMedia.FfmpegFactAttribute(VideoCodec.Hevc).Skip;
}

/// <summary>El pool de D3D11VA: cuántas superficies reserva para referencias según el nivel y quién decide las de sobra.</summary>
public sealed class HardwarePoolTests(ITestOutputHelper output)
{
    private static readonly MediaSpec Hevc4K = new("pool-hevc-2160p-30", 3840, 2160, Rational.Fps30, TimeSpan.FromSeconds(4))
    {
        Codec = VideoCodec.Hevc,
        GopLength = 30,
        BFrames = 2,
    };

    [Theory]
    [InlineData(AVCodecID.AV_CODEC_ID_HEVC, 3840, 2160, 153, 0, 6)]
    [InlineData(AVCodecID.AV_CODEC_ID_HEVC, 1920, 1080, 123, 0, 6)]
    [InlineData(AVCodecID.AV_CODEC_ID_HEVC, 1280, 720, 123, 0, 12)]
    [InlineData(AVCodecID.AV_CODEC_ID_HEVC, 640, 360, 93, 0, 16)]
    [InlineData(AVCodecID.AV_CODEC_ID_H264, 1920, 1080, 40, 0, 4)]
    [InlineData(AVCodecID.AV_CODEC_ID_H264, 1920, 1080, 40, 6, 6)]
    [InlineData(AVCodecID.AV_CODEC_ID_H264, 1920, 1080, 51, 0, 16)]
    [InlineData(AVCodecID.AV_CODEC_ID_H264, 3840, 2160, 51, 0, 5)]
    public void The_level_bounds_the_reference_frames(AVCodecID codec, int width, int height, int level, int declared, int expected) =>
        Assert.Equal(expected, VideoDecoder.LevelReferenceFrames(codec, width, height, level, declared));

    [Theory]
    [InlineData(AVCodecID.AV_CODEC_ID_HEVC, 0)]
    [InlineData(AVCodecID.AV_CODEC_ID_HEVC, -99)]
    [InlineData(AVCodecID.AV_CODEC_ID_HEVC, 77)]
    [InlineData(AVCodecID.AV_CODEC_ID_VP9, 51)]
    public void Without_a_known_level_or_codec_nothing_is_assumed(AVCodecID codec, int level) =>
        Assert.Null(VideoDecoder.LevelReferenceFrames(codec, 3840, 2160, level, 0));

    [HevcHardwareFact]
    public void A_compact_4k_pool_decodes_every_frame_exactly_while_the_cache_holds_its_frames()
    {
        string path = TestMediaGenerator.CreateDefault()!.GetOrCreate(Hevc4K).Path;
        GpuDevice gpu = MediaTestEnvironment.Gpu!;
        (HardwarePoolRequest full, DecoderSurfacePool fullPool, long fullVram) = Measure(gpu, path, compact: false, retain: 0, frames: 2);
        (HardwarePoolRequest compact, DecoderSurfacePool compactPool, long compactVram) = Measure(gpu, path, compact: true, retain: 4, frames: 120);

        output.WriteLine($"4K HEVC: FFmpeg reserva {full.ReferenceSurfaces} superficies de {full.Width}x{full.Height} ({full.SurfaceBytes / 1048576.0:0.0} MB); " +
            $"compacto {compact.ReferenceSurfaces}. Pool completo {fullPool.Surfaces} sup. = {fullPool.TotalBytes / 1048576} MB (VRAM medida {fullVram / 1048576} MB); " +
            $"compacto {compactPool.Surfaces} sup. = {compactPool.TotalBytes / 1048576} MB (VRAM medida {compactVram / 1048576} MB).");
        Assert.True(compact.ReferenceSurfaces < full.ReferenceSurfaces);
        Assert.InRange(compact.ReferenceSurfaces, 6, 12);
        Assert.Equal(compact.ReferenceSurfaces + 10, compactPool.Surfaces);
    }

    /// <summary>Decodifica en orden reteniendo los últimos <paramref name="retain"/> cuadros, como la caché, y comprueba el código de cada uno.</summary>
    private static (HardwarePoolRequest Request, DecoderSurfacePool Pool, long Vram) Measure(GpuDevice gpu, string path, bool compact, int retain, int frames)
    {
        HardwarePoolRequest? seen = null;
        long before = gpu.QueryLocalVideoMemoryUsage() ?? 0;
        var options = new VideoDecoderOptions
        {
            Device = gpu,
            CompactReferencePool = compact,
            HardwarePoolSizer = request =>
            {
                seen = request;
                return 10;
            },
        };
        var held = new Queue<DecodedFrame>();
        using VideoDecoder decoder = VideoDecoder.Open(path, options);
        try
        {
            for (int i = 0; i < frames; i++)
            {
                DecodedFrame frame = decoder.GetFrame(i) ?? throw new InvalidOperationException($"Falta el cuadro {i}.");
                LumaImage luma = FrameReadback.ReadLuma(frame);
                Assert.Equal(i, FrameCode.Read(luma.Pixels, luma.Width, luma.Height, luma.Width));
                held.Enqueue(frame);
                while (held.Count > retain)
                {
                    held.Dequeue().Dispose();
                }
            }

            long vram = (gpu.QueryLocalVideoMemoryUsage() ?? 0) - before;
            Assert.Equal(0, decoder.Statistics.DecodeErrors);
            return (seen ?? throw new InvalidOperationException("No se pidió el tamaño del pool."), decoder.SurfacePool!, vram);
        }
        finally
        {
            while (held.Count > 0)
            {
                held.Dequeue().Dispose();
            }
        }
    }
}
