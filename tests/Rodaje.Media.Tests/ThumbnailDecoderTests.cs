using System.Diagnostics;
using Rodaje.Core.Timing;
using Rodaje.Media.Tests.Support;
using Rodaje.Media.Video;
using Rodaje.TestMedia;
using Xunit.Abstractions;

namespace Rodaje.Media.Tests;

public sealed class ThumbnailDecoderTests(ITestOutputHelper output)
{
    private static readonly ClipSpec H264FullHd = new("h264-1080p25-g50-bf2", ClipCodec.H264, 25, 1, 500, Width: 1920, Height: 1080, Gop: 50);
    private static readonly ClipSpec HevcFullHd = new("hevc-1080p25-g50-bf2", ClipCodec.Hevc, 25, 1, 500, Width: 1920, Height: 1080, Gop: 50);

    [FfmpegLibraryFact]
    public void Thumbnail_has_the_display_aspect_and_requested_height()
    {
        using ThumbnailDecoder decoder = ThumbnailDecoder.Open(TestClips.PathOf(TestClips.H264));
        BgraImage? image = decoder.Decode(Time.FromSeconds(2), 72);

        Assert.NotNull(image);
        Assert.Equal(72, image.Height);
        Assert.Equal(128, image.Width);
        Assert.Equal(128 * 72 * 4, image.Pixels.Length);
        Assert.True(image.Pixels.Where((_, i) => i % 4 == 3).All(a => a == 255));
        Assert.True(image.Pixels.Where((_, i) => i % 4 != 3).Distinct().Count() > 16, "La miniatura salió plana.");
    }

    [FfmpegLibraryFact]
    public void Different_instants_give_different_images_and_the_same_instant_the_same()
    {
        using ThumbnailDecoder decoder = ThumbnailDecoder.Open(TestClips.PathOf(TestClips.H264));
        byte[] a = decoder.Decode(Time.FromSeconds(1), 48)!.Pixels;
        byte[] b = decoder.Decode(Time.FromSeconds(7), 48)!.Pixels;
        byte[] again = decoder.Decode(Time.FromSeconds(1), 48)!.Pixels;

        Assert.False(a.AsSpan().SequenceEqual(b));
        Assert.True(a.AsSpan().SequenceEqual(again));
    }

    [FfmpegLibraryFact]
    public void A_filmstrip_in_order_decodes_forward_without_seeking_each_time()
    {
        using ThumbnailDecoder decoder = ThumbnailDecoder.Open(TestClips.PathOf(TestClips.Hevc));
        for (int i = 0; i < 20; i++)
        {
            Assert.NotNull(decoder.Decode(Time.FromSeconds(i * 0.45), 48));
        }

        Assert.True(decoder.Seeks <= 2, $"Buscó {decoder.Seeks} veces.");
    }

    [FfmpegLibraryFact]
    public void Past_the_end_returns_the_last_frame_and_before_the_start_the_first()
    {
        using ThumbnailDecoder decoder = ThumbnailDecoder.Open(TestClips.PathOf(TestClips.Hevc));
        Assert.NotNull(decoder.Decode(Time.FromSeconds(3600), 48));
        Assert.NotNull(decoder.Decode(Time.FromSeconds(-5), 48));
    }

    [FfmpegLibraryFact(Encoder = TestClips.TenBitEncoder)]
    public void Ten_bit_hevc_gives_a_thumbnail()
    {
        using ThumbnailDecoder decoder = ThumbnailDecoder.Open(TestClips.PathOf(TestClips.Hevc10));
        Assert.NotNull(decoder.Decode(Time.FromSeconds(2), 96));
    }

    [Theory]
    [InlineData(1920, 1080, 72, 128)]
    [InlineData(1080, 1920, 96, 54)]
    [InlineData(100, 10000, 80, 20)]
    [InlineData(0, 0, 90, 160)]
    public void Width_keeps_the_display_aspect_within_limits(int displayWidth, int displayHeight, int height, int expected) =>
        Assert.Equal(expected, ThumbnailSize.WidthFor(displayWidth, displayHeight, height));

    [Trait("Categoria", "Rendimiento")]
    [FfmpegLibraryFact]
    public void Latency_of_1080p_h264_thumbnails() => MeasureLatency(H264FullHd, "H.264");

    [Trait("Categoria", "Rendimiento")]
    [FfmpegLibraryFact]
    public void Latency_of_1080p_hevc_thumbnails() => MeasureLatency(HevcFullHd, "HEVC");

    private void MeasureLatency(ClipSpec spec, string label)
    {
        string clip = TestClips.PathOf(spec);
        var open = Stopwatch.StartNew();
        using ThumbnailDecoder decoder = ThumbnailDecoder.Open(clip);
        decoder.Decode(Time.FromSeconds(9.5), 72);
        open.Stop();

        var random = new Random(11);
        const int Seeks = 30;
        var samples = new List<double>(Seeks);
        for (int i = 0; i < Seeks; i++)
        {
            // Saltos hacia atrás o lejos: siempre buscan un keyframe.
            double seconds = random.NextDouble() * (spec.Seconds - 0.5);
            decoder.Decode(Time.FromSeconds(Math.Max(0, seconds - 5)), 72);
            var watch = Stopwatch.StartNew();
            decoder.Decode(Time.FromSeconds(seconds), 72);
            samples.Add(watch.Elapsed.TotalMilliseconds);
        }

        var strip = Stopwatch.StartNew();
        const int StripCount = 20;
        long seeksBefore = decoder.Seeks;
        decoder.Decode(Time.Zero, 72);
        for (int i = 0; i < StripCount; i++)
        {
            Assert.NotNull(decoder.Decode(Time.FromSeconds(i * spec.Seconds / StripCount), 72));
        }

        strip.Stop();
        samples.Sort();
        double median = samples[samples.Count / 2];
        double p90 = samples[(int)(samples.Count * 0.9)];
        output.WriteLine($"{label} 1080p (GOP 50, {Environment.ProcessorCount} hilos): abrir y primera miniatura {open.Elapsed.TotalMilliseconds:0} ms; " +
            $"miniatura con búsqueda: mediana {median:0.0} ms, p90 {p90:0.0} ms, peor {samples[^1]:0.0} ms; " +
            $"tira de {StripCount} en orden: {strip.Elapsed.TotalMilliseconds:0} ms ({strip.Elapsed.TotalMilliseconds / StripCount:0.0} ms cada una, {decoder.Seeks - seeksBefore} búsquedas).");
        // Junto con el resto de las pruebas la CPU está repartida: solo se exige que no se dispare. Las cifras de
        // referencia (unos 50 ms en H.264 y 65 ms en HEVC) se toman corriendo esta prueba sola.
        Assert.True(median < 1000, $"Mediana de {median:0} ms.");
    }
}
