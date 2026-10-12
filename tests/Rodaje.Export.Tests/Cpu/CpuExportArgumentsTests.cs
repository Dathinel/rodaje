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

    [Theory]
    [InlineData("h264_mf", "h264_metadata")]
    [InlineData("hevc_mf", "hevc_metadata")]
    public void Media_foundation_in_mov_writes_the_range_in_the_bitstream(string id, string filter)
    {
        IReadOnlyList<string> mov = CpuExportArguments.Build(ExportContainer.Mov, id, ExportPixelFormat.Nv12, null, 45).Video;
        IReadOnlyList<string> mp4 = CpuExportArguments.Build(ExportContainer.Mp4, id, ExportPixelFormat.Nv12, null, 45).Video;

        int bsf = mov.ToList().IndexOf("-bsf:v:0");
        Assert.True(bsf >= 0, string.Join(' ', mov));
        Assert.Equal($"{filter}=video_full_range_flag=0", mov[bsf + 1]);
        // En MP4 la caja «nclx» ya lleva el rango.
        Assert.DoesNotContain("-bsf:v:0", mp4);
    }

    [Fact]
    public void The_range_in_the_bitstream_follows_export_color()
    {
        EncoderSpec mf = CpuExportArguments.Spec("h264_mf");
        ExportSettings settings = CpuExportArguments.Settings(ExportContainer.Mov, "h264_mf", ExportPixelFormat.Nv12, null);
        ExportSettings full = settings with { Video = settings.Video! with { Color = ExportColor.Bt709Limited with { FullRange = true } } };

        EncoderArguments arguments = FfmpegArguments.Build(full, mf, ExportPixelFormat.Nv12, null, Rodaje.Core.Timing.Rational.Fps30,
            TimeSpan.FromSeconds(1));

        Assert.Contains("h264_metadata=video_full_range_flag=1", arguments.Video);
        Assert.Equal(["-bsf:v:0", "h264_metadata=video_full_range_flag=0"], FfmpegArguments.RangeInBitstream(mf, fullRange: false));
    }

    [Theory]
    [InlineData("h264_nvenc")]
    [InlineData("prores_ks")]
    [InlineData("libsvtav1")]
    public void Encoders_that_write_their_range_need_no_filter(string id) =>
        Assert.Empty(FfmpegArguments.RangeInBitstream(CpuExportArguments.Spec(id), fullRange: false));
}
