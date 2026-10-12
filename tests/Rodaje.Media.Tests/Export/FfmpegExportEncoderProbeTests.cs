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
         S..... = Subtitle
         ------
         V....D libsvtav1            SVT-AV1(Scalable Video Technology for AV1) encoder (codec av1)
         V....D libvpx-vp9           libvpx VP9 (codec vp9)
         V....D h264_nvenc           NVIDIA NVENC H.264 encoder (codec h264)
         VFS..D dnxhd                VC3/DNxHD
         A....D aac                  AAC (Advanced Audio Coding)
         A....D libopus              libopus Opus (codec opus)
         S..... ass                  ASS (Advanced SubStation Alpha) subtitle
        """;

    // Salida real de FFmpeg 9 con h264_nvenc en un equipo sin GPU NVIDIA (recortada).
    private const string NvencError = """
        [h264_nvenc @ 000001d2c4a1b2c0] OpenEncodeSessionEx failed: unsupported device (2): (no details)
        [h264_nvenc @ 000001d2c4a1b2c0] No capable devices found
        [vost#0:0/h264_nvenc @ 000001d2c49f8a40] Error while opening encoder - maybe incorrect parameters such as bit_rate, rate, width or height.
        [vf#0:0 @ 000001d2c4a0f100] Error sending frames to consumers: Function not implemented
        [vf#0:0 @ 000001d2c4a0f100] Task finished with error code: -38 (Function not implemented)
        [vf#0:0 @ 000001d2c4a0f100] Terminating thread with return code -38 (Function not implemented)
        [vost#0:0/h264_nvenc @ 000001d2c49f8a40] Could not open encoder before EOF
        [out#0/null @ 000001d2c49f7f00] Nothing was written into output file, because at least one of its streams received no packets.
        Conversion failed!
        """;

    /// <summary>Ejecutor de procesos de mentira: responde según los argumentos y apunta cada llamada.</summary>
    private sealed class FakeRunner(Func<IReadOnlyList<string>, (int, string, string)> respond)
    {
        private readonly List<IReadOnlyList<string>> _calls = [];

        public IReadOnlyList<IReadOnlyList<string>> Calls
        {
            get
            {
                lock (_calls)
                {
                    return [.. _calls];
                }
            }
        }

        public int ListCalls => Calls.Count(c => c.Contains("-encoders"));

        public int EncodeCalls => Calls.Count(c => !c.Contains("-encoders"));

        public FfmpegExportEncoderProbe Probe() => new((args, _) =>
        {
            lock (_calls)
            {
                _calls.Add(args);
            }

            return respond(args);
        }, TimeSpan.Zero);
    }

    private static (int, string, string) ListOk => (0, EncodersOutput, "");

    private static EncoderSpec Spec(string id) => ExportEncoderCatalog.Find(id)!;

    private static string After(IReadOnlyList<string> args, string flag) => args[args.ToList().IndexOf(flag) + 1];

    [Fact]
    public void The_encoder_list_keeps_video_and_audio_but_not_the_legend_or_subtitles()
    {
        HashSet<string> ids = FfmpegExportEncoderProbe.ParseEncoderList(EncodersOutput);

        Assert.Equal(["aac", "dnxhd", "h264_nvenc", "libopus", "libsvtav1", "libvpx-vp9"], ids.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Each_video_encoder_is_probed_with_its_own_pixel_format_profile_and_fastest_preset()
    {
        IReadOnlyList<string> svt = FfmpegExportEncoderProbe.ProbeArguments(Spec("libsvtav1"));
        IReadOnlyList<string> dnx = FfmpegExportEncoderProbe.ProbeArguments(Spec("dnxhd"));
        IReadOnlyList<string> nvenc = FfmpegExportEncoderProbe.ProbeArguments(Spec("h264_nvenc"));
        IReadOnlyList<string> prores = FfmpegExportEncoderProbe.ProbeArguments(Spec("prores_ks"));

        Assert.Contains("d=1", After(svt, "-i"), StringComparison.Ordinal);
        Assert.Equal("libsvtav1", After(svt, "-c:v"));
        Assert.Equal("yuv420p", After(svt, "-pix_fmt"));
        Assert.Equal("13", After(svt, "-preset"));
        Assert.Equal(["-f", "null", "-"], svt.TakeLast(3));

        // Con yuv420p y sin perfil, dnxhd falla aunque funcione: hace falta su formato y un perfil DNxHR.
        Assert.Equal("yuv422p", After(dnx, "-pix_fmt"));
        Assert.Equal("dnxhr_hq", After(dnx, "-profile:v"));

        Assert.Equal("nv12", After(nvenc, "-pix_fmt"));
        Assert.Equal("high", After(nvenc, "-profile:v"));
        Assert.Equal("p1", After(nvenc, "-preset"));

        Assert.Equal("yuv422p10le", After(prores, "-pix_fmt"));
        Assert.Equal("hq", After(prores, "-profile:v"));
    }

    [Fact]
    public void The_probe_keeps_the_fixed_arguments_of_the_catalog()
    {
        IReadOnlyList<string> mf = FfmpegExportEncoderProbe.ProbeArguments(Spec("h264_mf"));
        IReadOnlyList<string> kvazaar = FfmpegExportEncoderProbe.ProbeArguments(Spec("libkvazaar"));
        IReadOnlyList<string> mjpeg = FfmpegExportEncoderProbe.ProbeArguments(Spec("mjpeg"));

        Assert.Equal("0", After(mf, "-hw_encoding"));
        Assert.Equal("preset=medium", After(kvazaar, "-kvazaar-params"));
        Assert.DoesNotContain("-crf", kvazaar);
        Assert.DoesNotContain("-preset", kvazaar);
        Assert.Equal("unofficial", After(mjpeg, "-strict"));
    }

    [Fact]
    public void Audio_encoders_are_probed_with_a_tone_at_their_own_rate()
    {
        IReadOnlyList<string> aacMf = FfmpegExportEncoderProbe.ProbeArguments(Spec("aac_mf"));
        IReadOnlyList<string> opus = FfmpegExportEncoderProbe.ProbeArguments(Spec("libopus"));
        IReadOnlyList<string> pcm = FfmpegExportEncoderProbe.ProbeArguments(Spec("pcm_s24le"));
        IReadOnlyList<string> flac = FfmpegExportEncoderProbe.ProbeArguments(Spec("flac"));

        Assert.StartsWith("sine=", After(aacMf, "-i"), StringComparison.Ordinal);
        Assert.Equal("aac_mf", After(aacMf, "-c:a"));
        Assert.Equal("192k", After(aacMf, "-b:a"));
        Assert.Equal("44100", After(aacMf, "-ar"));
        Assert.Equal("2", After(aacMf, "-ac"));
        Assert.DoesNotContain("-c:v", aacMf);
        Assert.DoesNotContain("-pix_fmt", aacMf);
        Assert.Equal("48000", After(opus, "-ar"));
        Assert.DoesNotContain("-b:a", pcm);
        Assert.DoesNotContain("-b:a", flac);
        Assert.Equal("pcm_s24le", After(pcm, "-c:a"));
    }

    [Fact]
    public void An_encoder_that_is_not_in_the_build_is_not_compiled_and_is_not_run()
    {
        var runner = new FakeRunner(args => args.Contains("-encoders") ? ListOk : (0, "", ""));

        ExportProbeResult result = runner.Probe().Probe(Spec("libkvazaar"));

        Assert.Equal(ExportProbeStatus.NotCompiled, result.Status);
        Assert.Contains("no está compilado", result.Reason, StringComparison.Ordinal);
        Assert.Equal(0, runner.EncodeCalls);
    }

    [Fact]
    public void A_failed_encode_is_failed_with_the_useful_lines()
    {
        var runner = new FakeRunner(args => args.Contains("-encoders") ? ListOk : (-22, "", NvencError));

        ExportProbeResult result = runner.Probe().Probe(Spec("h264_nvenc"));

        // De hardware y sin GPU NVIDIA: falla como «sin dispositivo», que no se reintenta (HardwareProbeStatusTests).
        Assert.Equal(ExportProbeStatus.NoDevice, result.Status);
        Assert.False(result.Works);
        Assert.Equal("h264_nvenc no funciona en este equipo: OpenEncodeSessionEx failed: unsupported device (2): (no details) | No capable devices found",
            result.Reason);
        Assert.Equal(1, runner.EncodeCalls);
    }

    [Fact]
    public void A_working_encode_works_and_the_list_is_read_once()
    {
        var runner = new FakeRunner(args => args.Contains("-encoders") ? ListOk : (0, "", ""));
        FfmpegExportEncoderProbe probe = runner.Probe();

        Assert.True(probe.Probe(Spec("libsvtav1")).Works);
        Assert.True(probe.Probe(Spec("aac")).Works);

        Assert.Equal(1, runner.ListCalls);
    }

    [Fact]
    public void A_list_that_could_not_be_read_is_a_failure_and_is_read_again_next_time()
    {
        int listCalls = 0;
        var runner = new FakeRunner(args =>
        {
            if (!args.Contains("-encoders"))
            {
                return (0, "", "");
            }

            // La primera vez, el exe termina mal (por ejemplo, bloqueado por el antivirus); la segunda, bien.
            return Interlocked.Increment(ref listCalls) == 1 ? (1, "", "Access denied.") : ListOk;
        });
        FfmpegExportEncoderProbe probe = runner.Probe();

        ExportProbeResult first = probe.Probe(Spec("libsvtav1"));
        ExportProbeResult second = probe.Probe(Spec("libsvtav1"));

        Assert.Equal(ExportProbeStatus.Failed, first.Status);
        Assert.Contains("código 1", first.Reason, StringComparison.Ordinal);
        Assert.Contains("Access denied.", first.Reason, StringComparison.Ordinal);
        Assert.True(second.Works);
        Assert.Equal(2, runner.ListCalls);
    }

    [Fact]
    public void An_empty_list_is_not_taken_as_nothing_compiled()
    {
        var runner = new FakeRunner(args => args.Contains("-encoders") ? (0, "Encoders:\n", "") : (0, "", ""));

        ExportProbeResult result = runner.Probe().Probe(Spec("libsvtav1"));

        Assert.Equal(ExportProbeStatus.Failed, result.Status);
    }

    [Fact]
    public void A_probe_that_timed_out_is_retried_once()
    {
        int encodes = 0;
        var runner = new FakeRunner(args => args.Contains("-encoders") ? ListOk
            : Interlocked.Increment(ref encodes) == 1 ? (FfmpegExportEncoderProbe.TimedOutExitCode, "", "no respondió en 60 s.") : (0, "", ""));

        Assert.True(runner.Probe().Probe(Spec("h264_nvenc")).Works);
        Assert.Equal(2, runner.EncodeCalls);
    }

    [Fact]
    public void A_real_error_is_not_retried_on_the_spot()
    {
        var runner = new FakeRunner(args => args.Contains("-encoders") ? ListOk : (-22, "", NvencError));

        runner.Probe().Probe(Spec("h264_nvenc"));

        Assert.Equal(1, runner.EncodeCalls);
    }

    [Fact]
    public void Reset_reads_the_list_again()
    {
        var runner = new FakeRunner(args => args.Contains("-encoders") ? ListOk : (0, "", ""));
        FfmpegExportEncoderProbe probe = runner.Probe();
        probe.Probe(Spec("libsvtav1"));

        probe.Reset();
        probe.Probe(Spec("libsvtav1"));

        Assert.Equal(2, runner.ListCalls);
    }

    [Fact]
    public void Useful_lines_fall_back_to_the_non_generic_ones()
    {
        const string error = """
            [vost#0:0 @ 0000] Unknown encoder 'libkvazaar'
            [vost#0:0 @ 0000] Error selecting an encoder
            Error opening output file -.
            Error opening output files: Encoder not found
            """;

        Assert.Equal("Unknown encoder 'libkvazaar' | Error selecting an encoder | Error opening output files: Encoder not found",
            FfmpegExportEncoderProbe.UsefulLines(error, "libkvazaar"));
        Assert.Equal("Conversion failed!", FfmpegExportEncoderProbe.UsefulLines("Conversion failed!\n", "x"));
        Assert.Equal("sin mensaje de error.", FfmpegExportEncoderProbe.UsefulLines("", "x"));
    }

    [ProxyFact]
    public async Task The_real_probe_lists_what_this_machine_can_encode()
    {
        FfmpegExecutable ffmpeg = ProxyTestEnvironment.Executable!;
        var availability = new ExportEncoderAvailability(new FfmpegExportEncoderProbe(ffmpeg));

        ExportEncoderReport report = await availability.GetAsync();
        string table = string.Join(Environment.NewLine, report.All.Select(s => $"{s.Encoder.Id}: {s.Result.Status} {s.Reason}"));

        Assert.Equal(ExportEncoderCatalog.All.Count, report.All.Count);
        Assert.All(report.All, s => Assert.Equal(s.Works, s.Reason is null));

        // Lo que la compilación LGPL trae siempre y codifica sin hardware.
        foreach (string id in new[] { "libsvtav1", "libvpx-vp9", "prores_ks", "dnxhd", "ffv1", "png", "aac", "libopus", "flac", "pcm_s24le" })
        {
            Assert.True(report.Find(id)!.Works, $"{id} debería funcionar en cualquier equipo.{Environment.NewLine}{table}");
        }

        // En Windows con Media Foundation (también en la CI), el H.264 de Windows por CPU es el primer recurso sin GPU.
        Assert.True(report.Find("h264_mf")!.Works, $"h264_mf (MFT de software) debería funcionar en Windows.{Environment.NewLine}{table}");
        Assert.True(report.Find("aac_mf")!.Works, $"aac_mf debería funcionar en Windows.{Environment.NewLine}{table}");

        ExportEncoderChoice choice = report.ChooseDefault()!;
        Assert.True(choice.Encoder.InDefaults);
        Assert.Equal(ExportCodec.H264, choice.Encoder.Codec);
        Assert.Equal("aac_mf", report.ChooseDefaultAudio()!.Encoder.Id);
        Assert.Same(report, await availability.GetAsync());
    }
}
