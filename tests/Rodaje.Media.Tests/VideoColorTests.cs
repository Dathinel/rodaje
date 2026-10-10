using FFmpeg.AutoGen.Abstractions;
using Rodaje.Media.Gpu;
using Rodaje.Media.Probe;
using Rodaje.Media.Tests.Support;
using Rodaje.Media.Video;
using Rodaje.Playback;
using Rodaje.TestMedia;

namespace Rodaje.Media.Tests;

public sealed class VideoColorTests
{
    [Theory]
    [InlineData(AVColorSpace.AVCOL_SPC_BT709, AVColorRange.AVCOL_RANGE_MPEG, AVPixelFormat.AV_PIX_FMT_YUV420P, 480, YuvMatrix.Bt709, false)]
    [InlineData(AVColorSpace.AVCOL_SPC_SMPTE170M, AVColorRange.AVCOL_RANGE_UNSPECIFIED, AVPixelFormat.AV_PIX_FMT_YUV420P, 1080, YuvMatrix.Bt601, false)]
    [InlineData(AVColorSpace.AVCOL_SPC_BT470BG, AVColorRange.AVCOL_RANGE_JPEG, AVPixelFormat.AV_PIX_FMT_YUV420P, 576, YuvMatrix.Bt601, true)]
    [InlineData(AVColorSpace.AVCOL_SPC_BT2020_NCL, AVColorRange.AVCOL_RANGE_MPEG, AVPixelFormat.AV_PIX_FMT_YUV420P10LE, 2160, YuvMatrix.Bt2020, false)]
    [InlineData(AVColorSpace.AVCOL_SPC_UNSPECIFIED, AVColorRange.AVCOL_RANGE_UNSPECIFIED, AVPixelFormat.AV_PIX_FMT_YUV420P, 576, YuvMatrix.Bt601, false)]
    [InlineData(AVColorSpace.AVCOL_SPC_UNSPECIFIED, AVColorRange.AVCOL_RANGE_UNSPECIFIED, AVPixelFormat.AV_PIX_FMT_YUV420P, 720, YuvMatrix.Bt709, false)]
    [InlineData(AVColorSpace.AVCOL_SPC_UNSPECIFIED, AVColorRange.AVCOL_RANGE_UNSPECIFIED, AVPixelFormat.AV_PIX_FMT_YUVJ420P, 1080, YuvMatrix.Bt709, true)]
    [InlineData(AVColorSpace.AVCOL_SPC_RGB, AVColorRange.AVCOL_RANGE_MPEG, AVPixelFormat.AV_PIX_FMT_YUVJ422P, 360, YuvMatrix.Bt601, false)]
    public void Declared_values_win_and_heuristics_fill_the_gaps(
        AVColorSpace space, AVColorRange range, AVPixelFormat format, int height, YuvMatrix matrix, bool full) =>
        Assert.Equal(new VideoColor(matrix, full), VideoColor.Resolve(space, range, format, height));

    [Fact]
    public void Conversion_through_swscale_keeps_what_the_samples_mean()
    {
        var bt709Full = new VideoColor(YuvMatrix.Bt709, true);
        Assert.Equal(new VideoColor(YuvMatrix.Bt709, false), FrameUploader.ConvertedColor(AVPixelFormat.AV_PIX_FMT_YUVJ422P, bt709Full));
        Assert.Equal(bt709Full, FrameUploader.ConvertedColor(AVPixelFormat.AV_PIX_FMT_YUV422P, bt709Full));
        Assert.Equal(new VideoColor(YuvMatrix.Bt601, false), FrameUploader.ConvertedColor(AVPixelFormat.AV_PIX_FMT_RGB24, bt709Full));
    }

    [FfmpegLibraryTheory]
    [InlineData(1, false, YuvMatrix.Bt709, false)]
    [InlineData(6, true, YuvMatrix.Bt601, true)]
    [InlineData(1, true, YuvMatrix.Bt709, true)]
    public void Decoded_frames_carry_the_color_the_stream_declares(int matrixCode, bool fullRange, YuvMatrix matrix, bool full)
    {
        string clip = TestClips.TaggedColor(matrixCode, fullRange);
        var expected = new VideoColor(matrix, full);
        Assert.Equal(expected, MediaProbe.Probe(clip).Video!.Color);

        using (VideoDecoder decoder = VideoDecoder.Open(clip, DecodeModes.Options(DecodeMode.SoftwareCpu)))
        using (DecodedFrame frame = decoder.GetFrame(10)!)
        {
            Assert.Equal(expected, Assert.IsType<CpuFramePayload>(frame.Payload).Color);
        }

        foreach (DecodeMode mode in Skip.HardwareDecoding is null
            ? new[] { DecodeMode.Hardware, DecodeMode.SoftwareUpload }
            : MediaTestEnvironment.Gpu is not null ? new[] { DecodeMode.SoftwareUpload } : [])
        {
            using VideoDecoder decoder = VideoDecoder.Open(clip, DecodeModes.Options(mode));
            using DecodedFrame frame = decoder.GetFrame(10)!;
            Assert.Equal(expected, Assert.IsType<D3D11TexturePayload>(frame.Payload).Color);
        }
    }

    [FfmpegLibraryFact]
    public void An_untagged_sd_clip_uses_bt601()
    {
        VideoStreamInfo video = MediaProbe.Probe(TestClips.PathOf(TestClips.H264)).Video!;
        Assert.Equal(360, video.Height);
        Assert.Equal(new VideoColor(YuvMatrix.Bt601, false), video.Color);
    }
}
