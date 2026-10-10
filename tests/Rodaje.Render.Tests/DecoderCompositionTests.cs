using Rodaje.Core.Timing;
using Rodaje.Media;
using Rodaje.Media.Gpu;
using Rodaje.Media.Video;
using Rodaje.Playback;
using Rodaje.Playback.Composition;
using Rodaje.Render.Tests.Support;
using Rodaje.TestMedia;

namespace Rodaje.Render.Tests;

/// <summary>Necesita ffmpeg.exe para generar el clip, FFmpeg y una GPU con D3D11VA; se omite con el motivo si falta algo.</summary>
internal sealed class DecoderGpuFactAttribute : FactAttribute
{
    private static readonly Lazy<string?> Reason = new(() =>
    {
        if (!FfmpegLibrary.TryInitialize(out string? ffmpeg))
        {
            return "Omitida: " + ffmpeg;
        }

        using GpuDevice? gpu = GpuDevice.TryCreate(out string? failure);
        if (gpu is null)
        {
            return "Omitida: " + failure;
        }

        return gpu.SupportsHardwareDecoding ? null : $"Omitida: la GPU {gpu.AdapterName} no ofrece D3D11VA a FFmpeg.";
    });

    public DecoderGpuFactAttribute() =>
        Skip = new FfmpegFactAttribute(VideoCodec.H264, AudioCodec.Aac).Skip ?? Reason.Value;
}

public sealed class DecoderCompositionTests
{
    [DecoderGpuFact]
    public void Composes_decoder_surfaces_and_reads_the_frame_code_of_each_layer()
    {
        GeneratedMedia clip = TestMediaGenerator.CreateDefault()!.GetOrCreate(TestMediaPresets.H264Ntsc24Aac);
        using GpuDevice gpu = GpuDevice.Create();
        using VideoDecoder decoder = VideoDecoder.Open(clip.Path, new VideoDecoderOptions { Device = gpu });
        using var compositor = new Compositor(gpu, 1920, 1080);

        foreach ((long background, long inset) in new[] { (0L, 5L), (40L, 12L), (100L, 99L), (101L, 300L) })
        {
            // El código del cuadro está arriba a la izquierda; la imagen en imagen va en el cuarto inferior derecho.
            using var frame = new CompositionFrame(Time.Zero, 1920, 1080,
            [
                Layer(decoder, background, LayerTransform.Identity, 0),
                Layer(decoder, inset, new LayerTransform(480, 270, 0.5, 0, 1), 1),
            ]);
            Assert.Equal(new CompositeResult(2, 0), compositor.Render(frame));
            var image = new RgbaImage(compositor.ReadbackRgba(), 1920, 1080);

            Assert.Equal((int)background, FrameCode.Read(image.Green(0, 0, 1920, 1080), 1920, 1080, 1920));
            Assert.Equal((int)inset, FrameCode.Read(image.Green(960, 540, 960, 540), 960, 540, 960));
        }

        Assert.InRange(compositor.CachedViewCount, 1, 64);
        compositor.ReleaseCachedViews();
        Assert.Equal(0, compositor.CachedViewCount);
    }

    private static LayerFrame Layer(VideoDecoder decoder, long index, LayerTransform transform, int track)
    {
        DecodedFrame frame = decoder.GetFrame(index) ?? throw new InvalidOperationException($"No se pudo decodificar el cuadro {index}.");
        Assert.True(Assert.IsType<D3D11TexturePayload>(frame.Payload).IsDecoderSurface);
        return new LayerFrame(track, $"clip-{track}", frame, transform);
    }
}
