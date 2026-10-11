using Rodaje.Media.Export;
using Rodaje.Media.Proxies;
using Rodaje.Media.Tests.Support;
using Rodaje.TestMedia;

namespace Rodaje.Media.Tests.Export;

public sealed class FfmpegExportEncoderProbeTests
{
    private const string EncodersOutput = """
        Encoders:
         V..... = Video
         A..... = Audio
         ------
         V....D libsvtav1            SVT-AV1(Scalable Video Technology for AV1) encoder (codec av1)
         V....D libvpx-vp9           libvpx VP9 (codec vp9)
         V....D h264_nvenc           NVIDIA NVENC H.264 encoder (codec h264)
         A....D libopus              libopus Opus (codec opus)
         VFS... mpeg4                MPEG-4 part 2
        """;

    [Fact]
    public void The_encoder_list_keeps_only_video_encoders()
    {
        HashSet<string> ids = FfmpegExportEncoderProbe.ParseEncoderList(EncodersOutput);

        Assert.Contains("libsvtav1", ids);
        Assert.Contains("libvpx-vp9", ids);
        Assert.Contains("h264_nvenc", ids);
        Assert.Contains("mpeg4", ids);
        Assert.DoesNotContain("libopus", ids);
        Assert.DoesNotContain("=", ids);
        Assert.DoesNotContain("------", ids);
    }

    [Fact]
    public void The_probe_encodes_one_second_with_the_fast_draft_arguments()
    {
        ExportEncoderInfo svt = ExportEncoderCatalog.Find("libsvtav1")!;

        IReadOnlyList<string> args = FfmpegExportEncoderProbe.ProbeArguments(svt);

        Assert.Contains("d=1", args[args.ToList().IndexOf("-i") + 1], StringComparison.Ordinal);
        Assert.Equal("libsvtav1", args[args.ToList().IndexOf("-c:v") + 1]);
        Assert.Equal("yuv420p", args[args.ToList().IndexOf("-pix_fmt") + 1]);
        Assert.Equal(["-f", "null", "-"], args.TakeLast(3));
    }

    [ProxyFact]
    public async Task The_real_probe_lists_what_this_machine_can_encode()
    {
        FfmpegExecutable ffmpeg = ProxyTestEnvironment.Executable!;
        var availability = new ExportEncoderAvailability(new FfmpegExportEncoderProbe(ffmpeg));

        ExportEncoderReport report = await availability.GetAsync();

        Assert.Equal(ExportEncoderCatalog.All.Length, report.All.Length);
        foreach (ExportEncoderStatus status in report.All)
        {
            Assert.Equal(status.Works, status.Reason is null);
        }

        // La compilación LGPL trae al menos un codificador de software (SVT-AV1, libaom, VP9, kvazaar u OpenH264).
        Assert.Contains(report.Available, e => !e.IsHardware);
        Assert.NotNull(report.ChooseDefault());
        Assert.NotNull(report.Choose(ExportCodec.H264));
        Assert.Same(report, await availability.GetAsync());
    }
}
