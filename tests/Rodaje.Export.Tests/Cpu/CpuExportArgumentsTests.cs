using Rodaje.Media.Export;

namespace Rodaje.Export.Tests.Cpu;

/// <summary>
/// Sin <c>ffmpeg.exe</c>: los ajustes de las exportaciones por CPU pasan el validador y <see cref="FfmpegArguments.Build"/>
/// les da los argumentos que esas pruebas esperan. Así un cambio del armado se ve también en Linux.
/// </summary>
public sealed class CpuExportArgumentsTests
{
    [Theory]
    [InlineData("h264_mf", ExportContainer.Mp4, ExportPixelFormat.Nv12, "aac_mf")]
    [InlineData("h264_mf", ExportContainer.Mov, ExportPixelFormat.Nv12, "aac")]
    [InlineData("libsvtav1", ExportContainer.Mp4, ExportPixelFormat.Yuv420P, "libopus")]
    [InlineData("libsvtav1", ExportContainer.Mkv, ExportPixelFormat.Yuv420P10, "libopus")]
    [InlineData("libvpx-vp9", ExportContainer.WebM, ExportPixelFormat.Yuv420P, "libopus")]
    [InlineData("prores_ks", ExportContainer.Mov, ExportPixelFormat.Yuv422P10, "pcm_s24le")]
    [InlineData("prores_ks", ExportContainer.Mov, ExportPixelFormat.Yuva444P10, "pcm_s16le")]
    [InlineData("dnxhd", ExportContainer.Mov, ExportPixelFormat.Yuv422P, "pcm_s16le")]
    [InlineData("dnxhd", ExportContainer.Mxf, ExportPixelFormat.Yuv422P10, "pcm_s24le")]
    [InlineData("ffv1", ExportContainer.Mkv, ExportPixelFormat.Yuv420P, "flac")]
    [InlineData("h264_mf", ExportContainer.Mp4, ExportPixelFormat.Nv12, null)]
    [InlineData(null, ExportContainer.M4a, null, "aac")]
    [InlineData(null, ExportContainer.Ogg, null, "libopus")]
    [InlineData(null, ExportContainer.Wav, null, "pcm_s24le")]
    public void The_cpu_cases_are_valid_settings(string? video, ExportContainer container, ExportPixelFormat? pixels, string? audio)
    {
        EncoderArguments arguments = CpuExportArguments.Build(container, video, pixels, audio, 45);

        Assert.Equal(1, arguments.PassCount);
        // Sin video, el audio lleva los del contenedor; el audio siempre lleva al menos la frecuencia y los canales.
        Assert.True(video is not null || arguments.Video.Count == 0);
        Assert.Equal(audio is not null, arguments.Audio.Count > 0);
    }

    [Fact]
    public void The_intra_only_encoders_get_no_gop_and_ffv1_carries_its_own()
    {
        foreach ((string id, ExportContainer container, ExportPixelFormat pixels) in new[]
        {
            ("prores_ks", ExportContainer.Mov, ExportPixelFormat.Yuv422P10),
            ("dnxhd", ExportContainer.Mxf, ExportPixelFormat.Yuv422P10),
            ("ffv1", ExportContainer.Mkv, ExportPixelFormat.Yuv420P),
        })
        {
            Assert.DoesNotContain("-g", CpuExportArguments.Build(container, id, pixels, null, 45).Video);
        }

        // El GOP de FFV1 va fijo en el catálogo: sin él FFmpeg pondría un cuadro clave cada 12.
        IReadOnlyList<string> ffv1 = CpuExportArguments.Spec("ffv1").BaseArguments;
        int g = ffv1.ToList().IndexOf("-g");
        Assert.True(g >= 0 && g + 1 < ffv1.Count, string.Join(' ', ffv1));
        Assert.Equal("1", ffv1[g + 1]);
    }

    [Fact]
    public void Inter_encoders_get_the_two_second_gop_and_svt_av1_the_quick_preset()
    {
        IReadOnlyList<string> av1 = CpuExportArguments.Build(ExportContainer.Mkv, "libsvtav1", ExportPixelFormat.Yuv420P, null, 45).Video;
        Assert.Equal(["-crf", "35", "-preset", CpuExportArguments.SvtAv1Preset, "-g", "60", "-flags", "+cgop"], av1);

        IReadOnlyList<string> vp9 = CpuExportArguments.Build(ExportContainer.WebM, "libvpx-vp9", ExportPixelFormat.Yuv420P, null, 45).Video;
        // FfmpegArguments no pone «+cgop» a libvpx.
        Assert.Equal(["-crf", "31", "-b:v", "0", "-g", "60"], vp9);
    }
}
