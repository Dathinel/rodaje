using FotoToma.Media.Gpu;
using FotoToma.Media.Tests.Support;
using FotoToma.Media.Video;
using FotoToma.Playback;

namespace FotoToma.Media.Tests;

/// <summary>Las referencias a texturas y al dispositivo vuelven a su valor inicial al liberar todo.</summary>
/// <remarks>Cada prueba usa su propio <see cref="GpuDevice"/> para contar sus referencias sin interferencias.</remarks>
public sealed class FrameLifetimeTests
{
    [HardwareFact]
    public void Hardware_frames_release_every_device_reference()
    {
        using GpuDevice device = GpuDevice.Create();
        uint baseline = device.GetDeviceReferenceCount();
        var payloads = new List<D3D11TexturePayload>();
        using (VideoDecoder decoder = VideoDecoder.Open(TestClips.PathOf(TestClips.Hevc), DecodeModes.Options(DecodeMode.Hardware, device)))
        {
            foreach (int n in VideoDecoderAccuracyTests.RandomRequestOrder(250, TestClips.KeyFrames(TestClips.Hevc)).Take(60))
            {
                using DecodedFrame frame = decoder.GetFrame(n)!;
                payloads.Add(Assert.IsType<D3D11TexturePayload>(frame.Payload));
            }

            Assert.True(decoder.UsesHardwareDecoding);
            Assert.True(device.GetDeviceReferenceCount() > baseline, "El pool de texturas del decodificador referencia el dispositivo.");
        }

        Assert.All(payloads, p => Assert.True(p.IsReleased));
        Assert.Equal(baseline, device.GetDeviceReferenceCount());
    }

    [HardwareFact]
    public void A_frame_outlives_its_decoder()
    {
        using GpuDevice device = GpuDevice.Create();
        uint baseline = device.GetDeviceReferenceCount();
        ulong expected = TestClips.LumaReference(TestClips.H264)[77];
        DecodedFrame kept;
        using (VideoDecoder decoder = VideoDecoder.Open(TestClips.PathOf(TestClips.H264), DecodeModes.Options(DecodeMode.Hardware, device)))
        {
            kept = decoder.GetFrame(77)!;
            decoder.GetFrame(200)!.Dispose();
        }

        Assert.Equal(expected, TestClips.HashLuma(FrameReadback.ReadLuma(kept).Pixels));
        Assert.True(device.GetDeviceReferenceCount() > baseline, "El cuadro retenido mantiene vivo el pool de texturas.");
        kept.Dispose();
        Assert.Equal(baseline, device.GetDeviceReferenceCount());
        Assert.Throws<ObjectDisposedException>(() => FrameReadback.ReadLuma(kept));
    }

    [HardwareFact]
    public void Payload_references_outlive_the_decoded_frame()
    {
        using GpuDevice device = GpuDevice.Create();
        using VideoDecoder decoder = VideoDecoder.Open(TestClips.PathOf(TestClips.H264), DecodeModes.Options(DecodeMode.Hardware, device));
        DecodedFrame frame = decoder.GetFrame(12)!;
        var texture = (D3D11TexturePayload)frame.Payload!;
        Assert.Equal(FramePixelFormat.Nv12, texture.PixelFormat);
        Assert.Equal(103, texture.DxgiFormat);
        Assert.Equal((640, 360), (texture.Width, texture.Height));
        Assert.True(texture.CodedWidth >= 640 && texture.CodedHeight >= 360);
        Assert.NotEqual(0, texture.Texture);

        Assert.True(texture.AddRef());
        frame.Dispose();
        frame.Dispose();
        Assert.False(texture.IsReleased);
        texture.Dispose();
        Assert.False(texture.IsReleased, "Dispose devuelve la referencia del dueño, que ya devolvió el cuadro.");

        texture.Release();
        Assert.True(texture.IsReleased);
        Assert.False(texture.AddRef());
        Assert.Throws<InvalidOperationException>(texture.Release);
    }

    [HardwareFact]
    public void Ten_bit_hardware_frames_are_p010()
    {
        using VideoDecoder decoder = VideoDecoder.Open(TestClips.PathOf(TestClips.Hevc10), DecodeModes.Options(DecodeMode.Hardware));
        using DecodedFrame frame = decoder.GetFrame(30)!;
        var texture = Assert.IsType<D3D11TexturePayload>(frame.Payload);
        Assert.Equal(FramePixelFormat.P010, texture.PixelFormat);
        Assert.Equal(104, texture.DxgiFormat);
        Assert.Equal((long)texture.CodedWidth * texture.CodedHeight * 3, frame.SizeBytes);
    }

    [HardwareFact]
    public void Uploaded_frames_release_their_own_textures()
    {
        using GpuDevice device = GpuDevice.Create();
        uint baseline = device.GetDeviceReferenceCount();
        var payloads = new List<D3D11TexturePayload>();
        using (VideoDecoder decoder = VideoDecoder.Open(TestClips.PathOf(TestClips.Hevc10), DecodeModes.Options(DecodeMode.SoftwareUpload, device)))
        {
            foreach (int n in new[] { 0, 50, 51, 119, 3 })
            {
                using DecodedFrame frame = decoder.GetFrame(n)!;
                var texture = Assert.IsType<D3D11TexturePayload>(frame.Payload);
                Assert.Equal(FramePixelFormat.P010, texture.PixelFormat);
                Assert.False(texture.IsDecoderSurface);
                payloads.Add(texture);
            }
        }

        Assert.All(payloads, p => Assert.True(p.IsReleased));
        Assert.Equal(baseline, device.GetDeviceReferenceCount());
    }

    [FfmpegFact]
    public void Cpu_frames_free_their_buffers()
    {
        using VideoDecoder decoder = VideoDecoder.Open(TestClips.PathOf(TestClips.H264), DecodeModes.Options(DecodeMode.SoftwareCpu));
        DecodedFrame frame = decoder.GetFrame(5)!;
        var cpu = Assert.IsType<CpuFramePayload>(frame.Payload);
        Assert.Equal(FramePixelFormat.Yuv420P, cpu.PixelFormat);
        ReadOnlySpan<byte> chroma = cpu.GetPlane(1, out int stride);
        Assert.True(stride >= 320);
        Assert.True(chroma.Length >= stride * 180);
        frame.Dispose();
        Assert.True(cpu.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() => cpu.GetPlane(0, out _));
    }

    [HardwareFact]
    public void Retained_frames_up_to_the_pool_margin_do_not_starve_the_decoder()
    {
        using VideoDecoder decoder = VideoDecoder.Open(TestClips.PathOf(TestClips.H264), DecodeModes.Options(DecodeMode.Hardware));
        IReadOnlyList<ulong> reference = TestClips.LumaReference(TestClips.H264);
        var kept = new List<DecodedFrame>();
        try
        {
            for (int n = 0; n < 12; n++)
            {
                kept.Add(decoder.GetFrame(n * 20)!);
            }

            for (int i = 0; i < kept.Count; i++)
            {
                Assert.Equal(reference[i * 20], TestClips.HashLuma(FrameReadback.ReadLuma(kept[i]).Pixels));
            }
        }
        finally
        {
            kept.ForEach(f => f.Dispose());
        }
    }
}
