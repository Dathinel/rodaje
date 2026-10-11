using Rodaje.Export.Tests.Support;
using Rodaje.Media.Export;
using Rational = Rodaje.Core.Timing.Rational;

namespace Rodaje.Export.Tests.Contract;

public sealed class MediaExportContractTests
{
    [Theory]
    [InlineData(ExportPixelFormat.Nv12, 1920, 1080, 3_110_400L)]
    [InlineData(ExportPixelFormat.P010, 1920, 1080, 6_220_800L)]
    [InlineData(ExportPixelFormat.Yuv420P, 3840, 2160, 12_441_600L)]
    [InlineData(ExportPixelFormat.Yuv422P10, 1920, 1080, 8_294_400L)]
    [InlineData(ExportPixelFormat.Yuv444P, 4, 2, 24L)]
    [InlineData(ExportPixelFormat.Yuva420P, 4, 2, 20L)]
    [InlineData(ExportPixelFormat.Yuva444P10, 2, 2, 32L)]
    [InlineData(ExportPixelFormat.Rgb24, 3, 1, 9L)]
    [InlineData(ExportPixelFormat.Rgba, 2, 2, 16L)]
    [InlineData(ExportPixelFormat.Rgba64, 1, 1, 8L)]
    public void Frame_size_is_the_packed_size_ffmpeg_reads_from_rawvideo(ExportPixelFormat format, int width, int height, long bytes) =>
        Assert.Equal(bytes, format.FrameBytes(width, height));

    [Fact]
    public void Every_pixel_format_has_a_name_depth_and_consistent_planes()
    {
        foreach (ExportPixelFormat format in Enum.GetValues<ExportPixelFormat>())
        {
            Assert.False(string.IsNullOrEmpty(format.FfmpegName()));
            Assert.Contains(format.BitDepth(), new[] { 8, 10, 16 });
            Assert.InRange(format.PlaneCount(), 1, 4);
            Assert.Equal(format.HasAlpha() && !format.IsRgb(), format.PlaneCount() == 4);
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => ((ExportPixelFormat)99).FfmpegName());
        Assert.Throws<ArgumentOutOfRangeException>(() => ExportPixelFormat.Nv12.PlaneSize(2, 4, 4));
        Assert.Equal((2, 1), ExportPixelFormat.Yuv420P.PlaneSize(1, 4, 2));
        Assert.Equal((4, 1), ExportPixelFormat.Nv12.PlaneSize(1, 4, 2));
        Assert.Equal((2, 2), ExportPixelFormat.Yuv422P.PlaneSize(2, 4, 2));
        Assert.Equal(ChromaSubsampling.Chroma444, ExportPixelFormat.Rgba.Chroma());
    }

    [Fact]
    public void Color_is_bt709_limited_in_hd_bt601_in_sd_and_srgb_for_images()
    {
        Assert.Equal(ExportColor.Bt709Limited, ExportColor.For(1920, 1080));
        Assert.Equal(ExportMatrix.Bt601, ExportColor.For(720, 576).Matrix);
        Assert.Equal(ExportColor.Srgb, ExportColor.For(1920, 1080, ExportPixelFormat.Rgba));
        Assert.Equal(["-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709", "-color_range", "tv",
            "-chroma_sample_location", "left"], ExportColor.Bt709Limited.FfmpegArguments());
        Assert.Equal("smpte170m", ExportColor.For(640, 480).FfmpegTransfer);
        Assert.Equal(("iec61966-2-1", "pc"), (ExportColor.Srgb.FfmpegTransfer, ExportColor.Srgb.FfmpegRange));
    }

    [Fact]
    public void Video_format_rejects_odd_sizes_only_where_chroma_is_subsampled()
    {
        Assert.Throws<ArgumentException>(() => new ExportVideoFormat(1919, 1080, Rational.Fps30, ExportPixelFormat.Nv12, ExportColor.Bt709Limited));
        Assert.Throws<ArgumentException>(() => new ExportVideoFormat(1920, 1081, Rational.Fps30, ExportPixelFormat.Yuv420P, ExportColor.Bt709Limited));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ExportVideoFormat(0, 2, Rational.Fps30, ExportPixelFormat.Nv12, ExportColor.Bt709Limited));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ExportVideoFormat(16386, 2, Rational.Fps30, ExportPixelFormat.Nv12, ExportColor.Bt709Limited));
        Assert.Equal(1081L * 2 * 2, new ExportVideoFormat(2, 1081, Rational.Fps30, ExportPixelFormat.Yuv422P, ExportColor.Bt709Limited).FrameBytes);
        Assert.Equal(6L, new ExportVideoFormat(1, 1, Rational.Fps30, ExportPixelFormat.Yuv444P10, ExportColor.Bt709Limited).FrameBytes);
    }

    [Fact]
    public void Audio_format_defaults_to_the_mix_and_validates_ranges()
    {
        Assert.Equal((48_000, 2, ExportSampleFormat.PcmFloat, 4), (ExportAudioFormat.Default.SampleRate, ExportAudioFormat.Default.Channels,
            ExportAudioFormat.Default.SampleFormat, ExportAudioFormat.Default.BytesPerSample));
        Assert.Equal(3, new ExportAudioFormat(sampleFormat: ExportSampleFormat.Pcm24).BytesPerSample);
        Assert.Equal(2, new ExportAudioFormat(44_100, 1, ExportSampleFormat.Pcm16).BytesPerSample);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ExportAudioFormat(4_000));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ExportAudioFormat(channels: 9));
    }

    [Fact]
    public void A_frame_with_pitch_padding_copies_packed_rows_plane_after_plane()
    {
        var format = new ExportVideoFormat(4, 2, Rational.Fps30, ExportPixelFormat.Nv12, ExportColor.Bt709Limited);
        byte[] luma = [1, 2, 3, 4, 0, 0, 5, 6, 7, 8, 0, 0];
        byte[] chroma = [9, 10, 11, 12];
        var frame = new ExportVideoFrame(7, format, luma, 6, chroma, 4);
        var packed = new byte[format.FrameBytes];

        Assert.Equal(12, frame.CopyPacked(packed));
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12], packed);
        Assert.Equal((7L, 2), (frame.Index, frame.PlaneCount));
        Assert.Equal([5, 6, 7, 8], frame.Row(0, 1).ToArray());
        Assert.Equal(4, frame.Pitch(1));
    }

    [Fact]
    public void A_frame_with_four_planes_and_no_padding_copies_in_one_block_per_plane()
    {
        var format = new ExportVideoFormat(2, 2, Rational.Fps30, ExportPixelFormat.Yuva420P, ExportColor.Bt709Limited);
        byte[] y = [1, 2, 3, 4];
        byte[] alpha = [7, 7, 7, 7];
        var frame = new ExportVideoFrame(0, format, y, 2, [5], 1, [6], 1, alpha, 2);
        var packed = new byte[format.FrameBytes];

        frame.CopyPacked(packed);
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 7, 7, 7], packed);
        Assert.Equal(7, frame.Plane(3)[0]);
        Assert.Equal(2, frame.Pitch(3));
    }

    [Fact]
    public void A_frame_rejects_short_planes_and_narrow_pitches()
    {
        var format = new ExportVideoFormat(4, 2, Rational.Fps30, ExportPixelFormat.Nv12, ExportColor.Bt709Limited);
        Assert.Throws<ArgumentException>(() => new ExportVideoFrame(0, format, new byte[7], 4, new byte[4], 4));
        Assert.Throws<ArgumentException>(() => new ExportVideoFrame(0, format, new byte[8], 3, new byte[4], 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ExportVideoFrame(-1, format, new byte[8], 4, new byte[4], 4));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            var frame = new ExportVideoFrame(0, format, new byte[8], 4, new byte[4], 4);
            return frame.Pitch(4);
        });
    }

    [Fact]
    public void Exceptions_carry_a_stable_code_and_say_when_the_user_can_continue_without()
    {
        var missing = new ExportException(ExportErrorCode.ContentMissing, "Falta la LUT.") { Frame = 12, Culprit = "lut@1/a" };
        Assert.True(missing.CanContinueWithout);
        Assert.Equal((12L, "lut@1/a"), (missing.Frame!.Value, missing.Culprit!));
        Assert.False(new ExportException(ExportErrorCode.DiskFull, "Lleno.").CanContinueWithout);
        Assert.Equal(ExportErrorCode.Unknown, new ExportException("x", new IOException()).Code);
        Assert.Equal(12, (int)ExportErrorCode.Canceled);
    }

    [Fact]
    public void Encoder_spec_picks_the_pixel_format_by_depth_chroma_and_alpha()
    {
        Assert.Equal(ExportPixelFormat.P010, TestEncoders.HevcNvenc.PixelFormatFor(10));
        Assert.Null(TestEncoders.H264Nvenc.PixelFormatFor(10));
        Assert.Equal(ExportPixelFormat.Yuva444P10, TestEncoders.ProRes.PixelFormatFor(10, ChromaSubsampling.Chroma444, alpha: true));
        Assert.True(TestEncoders.ProRes.SupportsAlpha && TestEncoders.ProRes.Supports10Bit);
        Assert.False(TestEncoders.AacMf.Supports10Bit || TestEncoders.AacMf.SupportsAlpha);
        Assert.True(TestEncoders.AacMf.IsAudio);
        Assert.False(TestEncoders.Kvazaar.InDefaults);
        Assert.True(new QualityScale("CRF", "-crf", 0, 51, 23).Contains(51));
        Assert.True(ExportCodec.Mjpeg.IsImage() && !ExportCodec.H264.IsImage());
    }
}
