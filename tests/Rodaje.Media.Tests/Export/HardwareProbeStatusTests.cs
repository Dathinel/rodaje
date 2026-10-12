using Rodaje.Media.Export;

namespace Rodaje.Media.Tests.Export;

/// <summary>
/// Qué dice la sonda real de un codificador de hardware que no abre: sin dispositivo (no se reintenta) o algo pasajero
/// (se reintenta). Sin procesos: el ejecutor responde lo que respondería FFmpeg.
/// </summary>
public sealed class HardwareProbeStatusTests
{
    private const string Encoders = """
         V....D h264_nvenc           NVIDIA NVENC H.264 encoder (codec h264)
         V....D av1_nvenc            NVIDIA NVENC av1 encoder (codec av1)
         V....D h264_amf             AMD AMF H.264 Encoder (codec h264)
         V....D libsvtav1            SVT-AV1(Scalable Video Technology for AV1) encoder (codec av1)
        """;

    private static ExportProbeResult ProbeWith(string id, int exitCode, string error) =>
        new FfmpegExportEncoderProbe((args, _) => args.Contains("-encoders") ? (0, Encoders, "") : (exitCode, "", error), TimeSpan.Zero)
            .Probe(ExportEncoderCatalog.Find(id)!);

    [Fact]
    public void Nvenc_without_an_nvidia_gpu_has_no_device()
    {
        ExportProbeResult result = ProbeWith("h264_nvenc", -22,
            "[h264_nvenc @ 0000] OpenEncodeSessionEx failed: unsupported device (2): (no details)\n[h264_nvenc @ 0000] No capable devices found\n");

        Assert.Equal(ExportProbeStatus.NoDevice, result.Status);
        Assert.Contains("No capable devices found", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Av1_nvenc_on_an_older_card_and_amf_without_its_driver_have_no_device()
    {
        Assert.Equal(ExportProbeStatus.NoDevice,
            ProbeWith("av1_nvenc", -38, "[av1_nvenc @ 0000] Codec not supported\n[av1_nvenc @ 0000] No capable devices found\n").Status);
        Assert.Equal(ExportProbeStatus.NoDevice,
            ProbeWith("h264_amf", -40, "[h264_amf @ 0000] DLL amfrt64.dll failed to open\n").Status);
    }

    [Theory]
    [InlineData("[h264_nvenc @ 0000] OpenEncodeSessionEx failed: out of memory (10): (no details)")]
    [InlineData("[h264_nvenc @ 0000] OpenEncodeSessionEx failed: incompatible client key (21): (no details)")]
    [InlineData("[h264_amf @ 0000] encoder is busy")]
    public void A_busy_or_full_gpu_is_a_failure_that_is_retried(string error)
    {
        string id = error.Contains("h264_amf", StringComparison.Ordinal) ? "h264_amf" : "h264_nvenc";

        Assert.Equal(ExportProbeStatus.Failed, ProbeWith(id, -12, error).Status);
    }

    [Fact]
    public void A_gpu_that_never_answers_is_a_failure_that_is_retried() =>
        Assert.Equal(ExportProbeStatus.Failed, ProbeWith("h264_nvenc", FfmpegExportEncoderProbe.TimedOutExitCode, "no respondió en 60 s.").Status);

    [Fact]
    public void A_software_encoder_that_fails_is_a_failure_not_a_missing_device() =>
        Assert.Equal(ExportProbeStatus.Failed, ProbeWith("libsvtav1", 1, "[libsvtav1 @ 0000] Svt[error]: out of range\n").Status);
}
