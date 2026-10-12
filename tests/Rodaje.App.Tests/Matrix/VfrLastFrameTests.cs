using Rodaje.Media.Video;
using Rodaje.TestMedia;

namespace Rodaje.App.Tests.Matrix;

/// <summary>
/// Regresión de la matriz: en un VFR cuyas marcas se repiten (75 cuadros, el último en 2,433 s a 30 nominal), el índice
/// 74 empieza justo donde termina el flujo y el decodificador no entregaba nada: el último cuadro del clip quedaba vacío.
/// </summary>
public sealed class VfrLastFrameTests
{
    [VfrFact]
    public void Every_counted_index_of_a_vfr_file_gives_a_frame()
    {
        string path = MatrixMedia.GetOrCreate(FfmpegTool.Default!, MatrixCases.Get("mp4-vfr-marcas-repetidas")).Path!;
        Assert.True(Rodaje.Media.FfmpegLibrary.TryInitialize(out string? failure), failure);
        using VideoDecoder decoder = VideoDecoder.Open(path, new VideoDecoderOptions { AllowHardwareDecoding = false });
        Assert.True(decoder.Stream.IsVariableFrameRate);

        for (long n = decoder.FrameCount - 3; n < decoder.FrameCount; n++)
        {
            using Rodaje.Playback.DecodedFrame? frame = decoder.GetFrame(n);
            Assert.NotNull(frame);
            LumaImage luma = FrameReadback.ReadLuma(frame);
            int? code = FrameCode.Read(luma.Pixels, luma.Width, luma.Height, luma.Width);
            Assert.True(code is { } c && Math.Abs(c - n) <= 1, $"Índice {n}: se vio {code}.");
        }

        Assert.Null(decoder.GetFrame(decoder.FrameCount));
    }
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class VfrFactAttribute : FactAttribute
{
    public VfrFactAttribute() => Skip = Rodaje.TestMedia.Skip.First(Rodaje.TestMedia.Skip.FfmpegLibraries,
        FfmpegTool.Default is { } ffmpeg ? ffmpeg.ProbeEncoder("h264_nvenc") : FfmpegTool.UnavailableReason);
}
