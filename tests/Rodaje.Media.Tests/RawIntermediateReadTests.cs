using Rodaje.Media.Gpu;
using Rodaje.Media.Video;
using Rodaje.Playback;
using Rodaje.TestMedia;

namespace Rodaje.Media.Tests;

public sealed class RawIntermediateReadTests
{
    [FfmpegLibraryFact(Encoder = "ffv1")]
    public void RawIntermediate_ffv1_pq_preserves_color_and_uploads_as_p016()
    {
        using var media = new RawIntermediateMedia();
        string path = media.CreatePq();
        using GpuDevice gpu = GpuDevice.CreateWarp();
        using VideoDecoder decoder = VideoDecoder.Open(path, new VideoDecoderOptions { Device = gpu, AllowHardwareDecoding = false });
        var expected = new VideoColor(YuvMatrix.Bt2020, true) { Transfer = HdrTransfer.Pq, Bt2020Primaries = true };
        Assert.Equal(expected, decoder.Stream.Color);
        Assert.Equal("ffv1", decoder.Stream.Codec);
        Assert.Equal("yuv444p16le", decoder.Stream.PixelFormat);
        Assert.Equal(16, decoder.Stream.BitDepth);
        Assert.Equal(RawIntermediateMedia.Width, decoder.Stream.Width);
        Assert.Equal(RawIntermediateMedia.Height, decoder.Stream.Height);
        using DecodedFrame frame = Assert.IsType<DecodedFrame>(decoder.GetFrame(0));
        var payload = Assert.IsType<D3D11TexturePayload>(frame.Payload);
        Assert.Equal(expected, payload.Color);
        Assert.Equal(FramePixelFormat.P016, payload.PixelFormat);
        Assert.False(decoder.UsesHardwareDecoding);
        Assert.Equal(0, decoder.Statistics.DecodeErrors);
    }
}
