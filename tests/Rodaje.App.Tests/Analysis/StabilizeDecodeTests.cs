using System.Diagnostics;
using Rodaje.Analysis.Video;
using Rodaje.Media.Video;
using Rodaje.Playback;
using Rodaje.TestMedia;
using Xunit.Abstractions;

namespace Rodaje.App.Tests.Analysis;

/// <summary>El análisis de «Estabilizar» con la decodificación de verdad: H.264 1080p en la CPU, como lo hace la app.</summary>
public sealed class StabilizeDecodeTests(ITestOutputHelper output)
{
    [FfmpegLibraryFact(VideoCodec.H264)]
    [Trait("Categoria", "TiempoReal")]
    public void A_1080p_clip_is_decoded_and_analyzed_at_60_fps_or_more()
    {
        string path = TestMediaGenerator.CreateDefault()!.GetOrCreate(TestMediaPresets.H264Ntsc30BFrames).Path;
        using VideoDecoder decoder = VideoDecoder.Open(path, new VideoDecoderOptions { AllowHardwareDecoding = false });
        var estimator = new MotionEstimator(decoder.Stream.Width, decoder.Stream.Height);
        const int count = 300;
        var watch = Stopwatch.StartNew();
        double decoding = 0;
        for (int i = 0; i < count; i++)
        {
            long before = Stopwatch.GetTimestamp();
            using DecodedFrame frame = decoder.GetFrame(i)!;
            decoding += Stopwatch.GetElapsedTime(before).TotalSeconds;
            var payload = (CpuFramePayload)frame.Payload!;
            ReadOnlySpan<byte> luma = payload.GetPlane(0, out int stride);
            estimator.Push(new LumaFrame(luma, payload.Width, payload.Height, stride, LumaFormat.Gray8));
        }

        watch.Stop();
        double fps = count / watch.Elapsed.TotalSeconds;
        output.WriteLine($"1080p H.264 en la CPU: {fps:F0} cuadros/s en total; decodificar {decoding / count * 1000:F2} ms y analizar " +
            $"{(watch.Elapsed.TotalSeconds - decoding) / count * 1000:F2} ms por cuadro");
        Assert.Equal(count, estimator.Motions.Count);
        Assert.True(fps >= 60, $"{fps:F0} cuadros/s");
    }
}
