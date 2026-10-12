using Rodaje.Media.Gpu;
using Rodaje.Media.Video;
using Rodaje.TestMedia;

namespace Rodaje.App.Tests.Matrix;

/// <summary>
/// Regresión de la matriz: la reversa de la exportación pedía un pool D3D11VA de 64 superficies y, con VP9, más de 48
/// dan cuadros negros (los 30 cuadros en reversa de un WebM salían ilegibles).
/// </summary>
public sealed class Vp9HardwarePoolTests
{
    [Vp9HardwareFact]
    public void A_vp9_decoder_asked_for_the_biggest_pool_still_gives_the_right_frame()
    {
        string path = MatrixMedia.GetOrCreate(FfmpegTool.Default!, MatrixCases.Get("webm-vp9-opus")).Path!;
        Assert.True(Rodaje.Media.FfmpegLibrary.TryInitialize(out string? failure), failure);
        using GpuDevice gpu = GpuDevice.Create();
        using VideoDecoder decoder = VideoDecoder.Open(path, new VideoDecoderOptions { Device = gpu, HardwarePoolSizer = _ => VideoDecoder.MaxHardwarePoolSurfaces });
        using Rodaje.Playback.DecodedFrame frame = decoder.GetFrame(39)!;

        Assert.True(decoder.UsesHardwareDecoding);
        Assert.Equal(VideoDecoder.MaxVp9HardwarePoolSurfaces, decoder.SurfacePool!.Surfaces);
        LumaImage luma = FrameReadback.ReadLuma(frame);
        Assert.Equal(39, FrameCode.Read(luma.Pixels, luma.Width, luma.Height, luma.Width));
    }
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class Vp9HardwareFactAttribute : FactAttribute
{
    public Vp9HardwareFactAttribute() => Skip = Rodaje.TestMedia.Skip.First(Rodaje.TestMedia.Skip.HardwareDecoding,
        FfmpegTool.Default is { } ffmpeg ? ffmpeg.ProbeEncoder("libvpx-vp9") : FfmpegTool.UnavailableReason);
}
