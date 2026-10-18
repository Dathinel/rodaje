using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Media.Probe;
using Rodaje.Media.Proxies;
using Rodaje.Media.Video;
using Rodaje.TestMedia;

namespace Rodaje.Media.Tests;

public sealed class StillImageTests
{
    [FfmpegLibraryTheory]
    [InlineData("png")]
    [InlineData("jpg")]
    [InlineData("webp")]
    [InlineData("bmp")]
    [InlineData("tiff")]
    public void Generated_images_are_supported_without_a_source_duration(string extension)
    {
        using var image = new StillImageFile(extension);
        Assert.Null(MediaSupport.Classify(image.Path, out MediaInfo? info));
        Assert.True(info!.IsStillImage);
        Assert.Equal(Time.Zero, info.Duration);
        Assert.Equal(1, info.Video!.FrameCount);
        var media = new MediaRef();
        info.ApplyTo(media);
        Assert.True(media.IsStillImage);
        Assert.Null(media.FrameRate);
        Assert.Equal(Time.Zero, media.Duration);
        Assert.Equal(StillImageFile.Width, media.Width);
        using var decoder = VideoDecoder.Open(image.Path);
        using var frame = decoder.GetFrame(0);
        Assert.NotNull(frame);
        Assert.False(new ProxyPolicy().Evaluate(ProxyCandidate.From(info) with { Width = 8000, Height = 6000 }).IsRecommended);
    }

    [FfmpegLibraryFact]
    public void The_decoded_png_is_retained_once_with_its_alpha_and_outlives_the_decoder()
    {
        using var image = new StillImageFile();
        using var decoder = VideoDecoder.Open(image.Path, new VideoDecoderOptions { AllowHardwareDecoding = false });
        using var first = decoder.GetFrame(0)!;
        var pixels = Assert.IsType<CpuFramePayload>(first.Payload);
        Assert.Equal("rgba", pixels.FfmpegPixelFormat);
        Assert.Equal(0, pixels.GetPlane(0, out _)[3]);
        Assert.Equal(128, pixels.GetPlane(0, out _)[(32 * 4) + 3]);
        Assert.Equal(255, pixels.GetPlane(0, out _)[(63 * 4) + 3]);
        for (int n = 0; n < 100; n++)
        {
            using var repeated = decoder.GetFrame(n);
            Assert.Same(first, repeated);
        }

        Assert.Equal(1, decoder.Statistics.DecodedFrames);
        decoder.Dispose();
        Assert.False(pixels.IsDisposed);
    }
}
