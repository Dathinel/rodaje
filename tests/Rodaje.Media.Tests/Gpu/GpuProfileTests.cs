using Rodaje.Media.Export;
using Rodaje.Media.Gpu;
using Rodaje.Media.Proxies;

namespace Rodaje.Media.Tests.Gpu;

/// <summary>Perfiles simulados de NVIDIA, AMD e Intel: generación, decodificación y qué codificadores se prueban.</summary>
public sealed class GpuProfileTests
{
    private const HardwareDecodeProfiles Everything = HardwareDecodeProfiles.H264 | HardwareDecodeProfiles.HevcMain
        | HardwareDecodeProfiles.HevcMain10 | HardwareDecodeProfiles.Vp9Profile0 | HardwareDecodeProfiles.Vp9Profile2
        | HardwareDecodeProfiles.Av1Main | HardwareDecodeProfiles.Mpeg2;

    public static GpuAdapter Rtx3050 { get; } = new("NVIDIA GeForce RTX 3050 Laptop GPU", 0x10DE, 0x25A2, 4L << 30, 8L << 30, false, "32.0.15.6094");

    public static GpuAdapter Rtx4070 { get; } = new("NVIDIA GeForce RTX 4070", 0x10DE, 0x2786, 12L << 30, 16L << 30, false, "32.0.15.6094");

    public static GpuAdapter Rx7800 { get; } = new("AMD Radeon RX 7800 XT", 0x1002, 0x747E, 16L << 30, 16L << 30, false, "31.0.24027.1012");

    public static GpuAdapter Rx6600 { get; } = new("AMD Radeon RX 6600", 0x1002, 0x73FF, 8L << 30, 16L << 30, false, "31.0.21912.14");

    public static GpuAdapter Rx6500 { get; } = new("AMD Radeon RX 6500 XT", 0x1002, 0x743F, 4L << 30, 16L << 30, false, "31.0.21912.14");

    public static GpuAdapter IrisXe { get; } = new("Intel(R) Iris(R) Xe Graphics", 0x8086, 0x9A49, 128L << 20, 8L << 30, false, "31.0.101.5186");

    public static GpuAdapter ArcA770 { get; } = new("Intel(R) Arc(TM) A770 Graphics", 0x8086, 0x56A0, 16L << 30, 16L << 30, false, "32.0.101.5762");

    public static GpuAdapter Warp { get; } = new("Microsoft Basic Render Driver", 0x1414, 0x8C, 0, 8L << 30, true, null);

    [Theory]
    [InlineData("NVIDIA GeForce RTX 3050 Laptop GPU", GpuVendor.Nvidia, "Ampere")]
    [InlineData("NVIDIA GeForce RTX 4090", GpuVendor.Nvidia, "Ada Lovelace")]
    [InlineData("NVIDIA GeForce RTX 5070 Ti", GpuVendor.Nvidia, "Blackwell")]
    [InlineData("NVIDIA GeForce GTX 1660 SUPER", GpuVendor.Nvidia, "Turing (TU117)")]
    [InlineData("NVIDIA GeForce RTX 2060", GpuVendor.Nvidia, "Turing")]
    [InlineData("NVIDIA GeForce GTX 1060 6GB", GpuVendor.Nvidia, "Pascal")]
    [InlineData("AMD Radeon RX 7800 XT", GpuVendor.Amd, "RDNA 3")]
    [InlineData("AMD Radeon 780M Graphics", GpuVendor.Amd, "RDNA 3")]
    [InlineData("AMD Radeon RX 6600", GpuVendor.Amd, "RDNA 2")]
    [InlineData("AMD Radeon RX 6500 XT", GpuVendor.Amd, "RDNA 2 (Navi 24)")]
    [InlineData("AMD Radeon RX 9070 XT", GpuVendor.Amd, "RDNA 4")]
    [InlineData("AMD Radeon RX 580 Series", GpuVendor.Amd, "GCN 4 (Polaris)")]
    [InlineData("Intel(R) Arc(TM) A770 Graphics", GpuVendor.Intel, "Xe-HPG (Alchemist)")]
    [InlineData("Intel(R) Arc(TM) B580 Graphics", GpuVendor.Intel, "Xe2 (Battlemage / Lunar Lake)")]
    [InlineData("Intel(R) Arc(TM) Graphics", GpuVendor.Intel, "Xe-LPG (Core Ultra)")]
    [InlineData("Intel(R) Iris(R) Xe Graphics", GpuVendor.Intel, "Xe-LP (Gen12: Tiger Lake a Raptor Lake)")]
    [InlineData("Intel(R) UHD Graphics 770", GpuVendor.Intel, "Xe-LP (Gen12: Tiger Lake a Raptor Lake)")]
    [InlineData("Intel(R) UHD Graphics 630", GpuVendor.Intel, "Gen9 (Skylake a Comet Lake)")]
    public void Known_adapters_map_to_their_generation(string name, GpuVendor vendor, string generation)
    {
        GpuGeneration found = GpuGenerations.Identify(vendor, name);

        Assert.True(found.Known);
        Assert.Equal(generation, found.Name);
    }

    [Fact]
    public void An_unknown_model_does_not_rule_anything_out()
    {
        GpuGeneration found = GpuGenerations.Identify(GpuVendor.Amd, "AMD Radeon(TM) Graphics");

        Assert.False(found.Known);
        var profile = new GpuProfile([Rx6600 with { Name = "AMD Radeon(TM) Graphics" }], Everything, 8);
        Assert.Null(profile.SkipReason(ExportEncoderCatalog.Find("av1_amf")!));
    }

    [Theory]
    [InlineData("h264", "yuv420p", 8, HardwareDecodeVerdict.Supported)]
    [InlineData("h264", "yuv422p10le", 10, HardwareDecodeVerdict.Unsupported)]
    [InlineData("h264", "yuv420p10le", 10, HardwareDecodeVerdict.Unsupported)]
    [InlineData("hevc", "yuv420p10le", 10, HardwareDecodeVerdict.Supported)]
    [InlineData("hevc", "yuv422p10le", 10, HardwareDecodeVerdict.Unsupported)]
    [InlineData("vp9", "yuv420p10le", 10, HardwareDecodeVerdict.Supported)]
    [InlineData("vp9", "yuv444p", 8, HardwareDecodeVerdict.Unsupported)]
    [InlineData("av1", "yuv420p10le", 10, HardwareDecodeVerdict.Supported)]
    [InlineData("mpeg2video", "yuv420p", 8, HardwareDecodeVerdict.Supported)]
    [InlineData("mpeg2video", "yuv422p", 8, HardwareDecodeVerdict.Unsupported)]
    [InlineData("vc1", "yuv420p", 8, HardwareDecodeVerdict.Unknown)]
    public void Nvidia_ampere_decode_verdicts(string codec, string pixels, int bits, HardwareDecodeVerdict expected) =>
        Assert.Equal(expected, HardwareDecodeSupport.Evaluate(codec, pixels, bits, ffmpegHasD3D11Va: true, Everything, GpuVendor.Nvidia));

    [Fact]
    public void Codecs_without_d3d11va_in_ffmpeg_go_to_software_threads()
    {
        Assert.Equal(HardwareDecodeVerdict.Unsupported,
            HardwareDecodeSupport.Evaluate("prores", "yuv422p10le", 10, ffmpegHasD3D11Va: false, Everything, GpuVendor.Nvidia));
        Assert.Equal(HardwareDecodeVerdict.Unsupported,
            HardwareDecodeSupport.Evaluate("dnxhd", "yuv422p", 8, ffmpegHasD3D11Va: false, Everything, GpuVendor.Amd));
    }

    [Fact]
    public void Intel_tries_hevc_422_because_ffmpeg_has_its_profiles()
    {
        Assert.Equal(HardwareDecodeVerdict.Unknown,
            HardwareDecodeSupport.Evaluate("hevc", "yuv422p10le", 10, ffmpegHasD3D11Va: true, Everything, GpuVendor.Intel));
        Assert.Equal(HardwareDecodeVerdict.Unsupported,
            HardwareDecodeSupport.Evaluate("hevc", "yuv422p10le", 10, ffmpegHasD3D11Va: true, Everything, GpuVendor.Amd));
    }

    [Fact]
    public void A_driver_without_av1_profiles_sends_av1_to_software()
    {
        HardwareDecodeProfiles polaris = Everything & ~(HardwareDecodeProfiles.Av1Main | HardwareDecodeProfiles.Vp9Profile2);

        Assert.Equal(HardwareDecodeVerdict.Unsupported, HardwareDecodeSupport.Evaluate("av1", "yuv420p", 8, true, polaris, GpuVendor.Amd));
        Assert.Equal(HardwareDecodeVerdict.Unsupported, HardwareDecodeSupport.Evaluate("vp9", "yuv420p10le", 10, true, polaris, GpuVendor.Amd));
        Assert.Equal(HardwareDecodeVerdict.Supported, HardwareDecodeSupport.Evaluate("hevc", "yuv420p10le", 10, true, polaris, GpuVendor.Amd));
    }

    [Fact]
    public void Unread_profiles_keep_trying_hardware() =>
        Assert.Equal(HardwareDecodeVerdict.Unknown,
            HardwareDecodeSupport.Evaluate("hevc", "yuv420p", 8, true, HardwareDecodeProfiles.None, GpuVendor.Nvidia));

    [Fact]
    public void Ampere_skips_only_av1_nvenc_and_the_other_vendors()
    {
        var profile = new GpuProfile([Rtx3050], Everything, 16);

        Assert.Null(profile.SkipReason(ExportEncoderCatalog.Find("h264_nvenc")!));
        Assert.Null(profile.SkipReason(ExportEncoderCatalog.Find("hevc_nvenc")!));
        Assert.Contains("Ampere", profile.SkipReason(ExportEncoderCatalog.Find("av1_nvenc")!), StringComparison.Ordinal);
        Assert.Contains("AMD", profile.SkipReason(ExportEncoderCatalog.Find("h264_amf")!), StringComparison.Ordinal);
        Assert.Contains("Intel", profile.SkipReason(ExportEncoderCatalog.Find("h264_qsv")!), StringComparison.Ordinal);
        Assert.Null(profile.SkipReason(ExportEncoderCatalog.Find("h264_mf")!));
        Assert.Null(profile.SkipReason(ExportEncoderCatalog.Find("libsvtav1")!));
        Assert.Equal(EncoderVendor.Nvidia, profile.PreferredEncoderVendor);
    }

    [Fact]
    public void A_hybrid_laptop_keeps_quick_sync_and_prefers_the_dedicated_gpu()
    {
        var profile = new GpuProfile([Rtx4070, IrisXe], Everything, 16);

        Assert.Null(profile.SkipReason(ExportEncoderCatalog.Find("av1_nvenc")!));
        Assert.Null(profile.SkipReason(ExportEncoderCatalog.Find("h264_qsv")!));
        Assert.NotNull(profile.SkipReason(ExportEncoderCatalog.Find("av1_qsv")!));
        Assert.NotNull(profile.SkipReason(ExportEncoderCatalog.Find("hevc_amf")!));
        Assert.Equal(EncoderVendor.Nvidia, profile.PreferredEncoderVendor);
    }

    [Fact]
    public void Amd_profiles_decide_amf_and_av1()
    {
        var rdna3 = new GpuProfile([Rx7800], Everything, 16);
        var rdna2 = new GpuProfile([Rx6600], Everything, 12);
        var navi24 = new GpuProfile([Rx6500], Everything, 12);

        Assert.Equal(EncoderVendor.Amd, rdna3.PreferredEncoderVendor);
        Assert.Null(rdna3.SkipReason(ExportEncoderCatalog.Find("av1_amf")!));
        Assert.NotNull(rdna2.SkipReason(ExportEncoderCatalog.Find("av1_amf")!));
        Assert.Null(rdna2.SkipReason(ExportEncoderCatalog.Find("hevc_amf")!));
        Assert.NotNull(navi24.SkipReason(ExportEncoderCatalog.Find("h264_amf")!));
        Assert.NotNull(rdna3.SkipReason(ExportEncoderCatalog.Find("h264_nvenc")!));
    }

    [Fact]
    public void Intel_arc_encodes_av1()
    {
        var arc = new GpuProfile([ArcA770], Everything, 16);

        Assert.Equal(EncoderVendor.Intel, arc.PreferredEncoderVendor);
        Assert.Null(arc.SkipReason(ExportEncoderCatalog.Find("av1_qsv")!));
        Assert.NotNull(arc.SkipReason(ExportEncoderCatalog.Find("av1_nvenc")!));
    }

    [Fact]
    public void Without_a_gpu_everything_hardware_is_skipped_and_cpu_stays()
    {
        var none = new GpuProfile([Warp], HardwareDecodeProfiles.None, 4);

        Assert.False(none.HasHardwareGpu);
        Assert.Null(none.PreferredEncoderVendor);
        Assert.All(ExportEncoderCatalog.Video.Where(e => e.IsHardware), e => Assert.NotNull(none.SkipReason(e)));
        Assert.Null(none.SkipReason(ExportEncoderCatalog.Find("h264_mf")!));
        Assert.Contains("Sin GPU", none.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void Proxies_are_encoded_by_the_main_gpu_first()
    {
        Assert.Equal(["h264_nvenc", "h264_mf", "libopenh264"], ProxyEncoders.PreferenceFor(new GpuProfile([Rtx3050], Everything, 16)));
        Assert.Equal(["h264_amf", "h264_mf", "libopenh264"], ProxyEncoders.PreferenceFor(new GpuProfile([Rx6600], Everything, 12)));
        Assert.Equal(["h264_qsv", "h264_mf", "libopenh264"], ProxyEncoders.PreferenceFor(new GpuProfile([ArcA770], Everything, 16)));
        Assert.Equal(["h264_nvenc", "h264_qsv", "h264_mf", "libopenh264"], ProxyEncoders.PreferenceFor(new GpuProfile([Rtx4070, IrisXe], Everything, 16)));
        Assert.Equal(["h264_mf", "libopenh264"], ProxyEncoders.PreferenceFor(new GpuProfile([Rx6500], Everything, 8)));
        Assert.Equal(["h264_mf", "libopenh264"], ProxyEncoders.PreferenceFor(new GpuProfile([Warp], HardwareDecodeProfiles.None, 4)));

        var input = new ProxyEncodeInput("a.mp4", 0, 3840, 2160, Rodaje.Core.Timing.Rational.Fps30);
        foreach (string encoder in new[] { "h264_amf", "h264_qsv" })
        {
            IReadOnlyList<string> args = ProxyEncoders.BuildArguments(input, ProxySettings.Default, encoder, "p.mp4", hardwareDecoding: true);
            Assert.Equal("nv12", args[args.ToList().IndexOf("-pix_fmt") + 1]);
            Assert.Contains("-maxrate", args);
            Assert.Equal("0", args[args.ToList().IndexOf("-bf") + 1]);
        }
    }

    [Fact]
    public void The_cache_key_follows_the_driver()
    {
        var before = new GpuProfile([Rtx3050], Everything, 16);
        var after = new GpuProfile([Rtx3050 with { DriverVersion = "32.0.15.7000" }], Everything, 16);

        Assert.NotEqual(before.Key, after.Key);
        Assert.Equal("32.0.15.6094", GpuProfileReader.FormatDriverVersion((32L << 48) | (0L << 32) | (15L << 16) | 6094));
    }

    [Fact]
    public void Reading_the_real_profile_never_throws_and_uses_the_cache()
    {
        string cache = Path.Combine(Path.GetTempPath(), $"rodaje-gpu-{Guid.NewGuid():N}.json");
        try
        {
            GpuProfile first = GpuProfileReader.Read(cache);
            GpuProfile second = GpuProfileReader.Read(cache);

            Assert.Equal(first.Key, second.Key);
            Assert.Equal(first.Decode, second.Decode);
            Assert.False(string.IsNullOrWhiteSpace(first.Describe()));
        }
        finally
        {
            File.Delete(cache);
        }
    }
}
