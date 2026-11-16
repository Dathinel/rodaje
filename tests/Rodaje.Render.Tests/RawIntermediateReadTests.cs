using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Media.Gpu;
using Rodaje.Media.Video;
using Rodaje.Playback;
using Rodaje.Playback.Composition;
using Rodaje.Render.Tests.Support;
using Rodaje.TestMedia;
using Xunit.Abstractions;

namespace Rodaje.Render.Tests;

public sealed class RawIntermediateReadTests(ITestOutputHelper output)
{
    [FfmpegLibraryFact(Encoder = "prores_ks")]
    public void RawIntermediate_hdr_recovers_two_stops_that_sdr_prores_loses()
    {
        using var media = new RawIntermediateMedia();
        string pq = media.CreatePq();
        string sdr = media.CreateSdr();
        using GpuDevice gpu = GpuDevice.CreateWarp();
        float[] plain = Render(gpu, pq, SequenceColorSpace.HdrPq, false);
        float[] exposed = Render(gpu, pq, SequenceColorSpace.HdrPq, true);
        float[] prores = Render(gpu, sdr, SequenceColorSpace.HdrPq, true);
        output.WriteLine($"812 nits en compositor HDR, RGB: {Patch(plain, 16)}; con −2 EV: {Patch(exposed, 16)}; ProRes SDR con −2 EV: {Patch(prores, 16)}.");
        output.WriteLine($"203 nits en compositor HDR, RGB: {Patch(plain, 48)}; con −2 EV: {Patch(exposed, 48)}.");
        AssertPatch(plain, 16, 4);
        AssertPatch(plain, 48, 1);
        AssertPatch(exposed, 16, 1);
        AssertPatch(exposed, 48, 0.25f);
        AssertPatch(prores, 16, 0.25f);
        AssertPatch(prores, 48, 0.25f);
    }

    [FfmpegLibraryFact(Encoder = "ffv1")]
    public void RawIntermediate_pq_maps_to_finite_sdr_on_warp()
    {
        using var media = new RawIntermediateMedia();
        using GpuDevice gpu = GpuDevice.CreateWarp();
        float[] pixels = Render(gpu, media.CreatePq(), SequenceColorSpace.Sdr709, false);
        Assert.All(pixels, value =>
        {
            Assert.True(float.IsFinite(value));
            Assert.InRange(value, 0, 1);
        });
        Assert.True(pixels[Index(16)] > pixels[Index(48)]);
        Assert.True(pixels[Index(48)] > 0);
        output.WriteLine($"Mapeo SDR, RGB: 812 nits → {Patch(pixels, 16)}; 203 nits → {Patch(pixels, 48)}.");
    }

    private static float[] Render(GpuDevice gpu, string path, SequenceColorSpace space, bool exposure)
    {
        using VideoDecoder decoder = VideoDecoder.Open(path, new VideoDecoderOptions { Device = gpu, AllowHardwareDecoding = false });
        using var compositor = new Compositor(gpu, RawIntermediateMedia.Width, RawIntermediateMedia.Height) { ColorSpace = space };
        Assert.True(compositor.ShadersReady.Wait(TimeSpan.FromSeconds(60)));
        // La referencia de 1000 nits permite distinguir los dos parches tras el mapeo SDR de la fase A.
        LayerTransform transform = LayerTransform.Identity with { HdrReferenceNits = 1000 };
        var layer = new LayerFrame(0, "parches", Assert.IsType<DecodedFrame>(decoder.GetFrame(0)), transform)
        {
            Effects = exposure ? [Fx.Color((BuiltInEffects.ColorParameters.Exposure, -2))] : [],
        };
        using var frame = new CompositionFrame(Time.Zero, RawIntermediateMedia.Width, RawIntermediateMedia.Height, [layer]);
        var result = compositor.RenderStrict(frame);
        Assert.Equal(0, result.LayersSkipped);
        Assert.Equal(0, result.EffectsSkipped);
        return compositor.ReadbackRgbaFloat();
    }

    private static int Index(int x) => (((RawIntermediateMedia.Height / 2) * RawIntermediateMedia.Width) + x) * 4;

    private static string Patch(float[] pixels, int x) => string.Join(", ", pixels.AsSpan(Index(x), 3).ToArray());

    private static void AssertPatch(float[] pixels, int x, float expected)
    {
        for (int channel = 0; channel < 3; channel++)
        {
            Assert.InRange(pixels[Index(x) + channel], expected * 0.99f, expected * 1.01f);
        }
    }
}
