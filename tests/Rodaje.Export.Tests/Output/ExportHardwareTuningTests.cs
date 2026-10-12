using Rodaje.Core.Timing;
using Rodaje.Export.Tuning;
using Rodaje.Media.Export;
using Rodaje.Media.Gpu;

namespace Rodaje.Export.Tests.Output;

/// <summary>Elección y preajustes por fabricante con perfiles simulados de NVIDIA, AMD, Intel y un equipo sin GPU.</summary>
public sealed class ExportHardwareTuningTests
{
    private static readonly GpuProfile Nvidia = Profile(new GpuAdapter("NVIDIA GeForce RTX 3050 Laptop GPU", 0x10DE, 0x25A2, 4L << 30, 0, false, "1"));
    private static readonly GpuProfile Amd = Profile(new GpuAdapter("AMD Radeon RX 6600", 0x1002, 0x73FF, 8L << 30, 0, false, "1"));
    private static readonly GpuProfile AmdWithIntel = Profile(
        new GpuAdapter("AMD Radeon RX 7800 XT", 0x1002, 0x747E, 16L << 30, 0, false, "1"),
        new GpuAdapter("Intel(R) UHD Graphics 770", 0x8086, 0xA780, 128L << 20, 0, false, "1"));
    private static readonly GpuProfile Intel = Profile(new GpuAdapter("Intel(R) Arc(TM) A770 Graphics", 0x8086, 0x56A0, 16L << 30, 0, false, "1"));
    private static readonly GpuProfile NoGpu = GpuProfile.CpuOnly(8);

    private static IReadOnlyList<EncoderSpec> Probed(GpuProfile profile) => ExportEncoderChooser.PreferVendor(
        ExportEncoderCatalog.All.Where(e => profile.SkipReason(e) is null), profile.PreferredEncoderVendor);

    [Fact]
    public void Default_h264_follows_the_vendor()
    {
        Assert.Equal("h264_nvenc", ExportEncoderChooser.ChooseDefault(Probed(Nvidia))!.Encoder.Id);
        Assert.Equal("h264_amf", ExportEncoderChooser.ChooseDefault(Probed(Amd))!.Encoder.Id);
        Assert.Equal("h264_qsv", ExportEncoderChooser.ChooseDefault(Probed(Intel))!.Encoder.Id);
        Assert.Equal("h264_mf", ExportEncoderChooser.ChooseDefault(Probed(NoGpu))!.Encoder.Id);
    }

    [Fact]
    public void The_main_gpu_goes_before_the_integrated_one()
    {
        IReadOnlyList<EncoderSpec> available = Probed(AmdWithIntel);

        Assert.Equal("h264_amf", available[0].Id);
        Assert.True(available.ToList().FindIndex(e => e.Id == "h264_qsv") < available.ToList().FindIndex(e => e.Id == "h264_mf"));
        Assert.Equal("hevc_amf", ExportPlanChoose(ExportCodec.Hevc, available));
        Assert.Equal("av1_amf", ExportPlanChoose(ExportCodec.Av1, available));
    }

    [Fact]
    public void Av1_goes_to_the_gpu_only_where_the_generation_has_it()
    {
        Assert.Equal("libsvtav1", ExportPlanChoose(ExportCodec.Av1, Probed(Nvidia)));
        Assert.Equal("libsvtav1", ExportPlanChoose(ExportCodec.Av1, Probed(Amd)));
        Assert.Equal("av1_qsv", ExportPlanChoose(ExportCodec.Av1, Probed(Intel)));
        Assert.Equal("libsvtav1", ExportPlanChoose(ExportCodec.Av1, Probed(NoGpu)));
    }

    [Theory]
    [InlineData("h264_nvenc", ExportPerformance.Fastest, "p4")]
    [InlineData("h264_nvenc", ExportPerformance.Background, "p4")]
    [InlineData("hevc_amf", ExportPerformance.Fastest, "speed")]
    [InlineData("hevc_amf", ExportPerformance.Background, "balanced")]
    [InlineData("h264_qsv", ExportPerformance.Fastest, "faster")]
    [InlineData("h264_qsv", ExportPerformance.Background, "medium")]
    [InlineData("libsvtav1", ExportPerformance.Fastest, "9")]
    [InlineData("libsvtav1", ExportPerformance.Background, "8")]
    public void Presets_per_vendor_and_mode(string id, ExportPerformance mode, string preset)
    {
        EncoderSpec encoder = ExportEncoderCatalog.Find(id)!;
        var video = new VideoExportSettings { Codec = encoder.Codec };

        IReadOnlyList<string> args = FfmpegArguments.Video(video, encoder, encoder.PixelFormats[0], Rational.Fps30, TimeSpan.FromSeconds(10), 192, mode);

        Assert.Equal(preset, args[args.ToList().IndexOf("-preset") + 1]);
    }

    [Fact]
    public void A_chosen_preset_always_wins()
    {
        EncoderSpec encoder = ExportEncoderCatalog.Find("h264_nvenc")!;
        var video = new VideoExportSettings { Codec = ExportCodec.H264, SpeedPreset = "p7" };

        IReadOnlyList<string> args = FfmpegArguments.Video(video, encoder, ExportPixelFormat.Nv12, Rational.Fps30, TimeSpan.FromSeconds(10), 192,
            ExportPerformance.Fastest);

        Assert.Equal("p7", args[args.ToList().IndexOf("-preset") + 1]);
    }

    [Fact]
    public void Gpu_or_cpu_encoding_per_export()
    {
        var h264 = new ExportSettings { Video = new VideoExportSettings { Codec = ExportCodec.H264 } };
        var av1 = new ExportSettings { Video = new VideoExportSettings { Codec = ExportCodec.Av1 } };
        var prores = new ExportSettings { Video = new VideoExportSettings { Codec = ExportCodec.ProRes } };
        var forcedCpu = new ExportSettings { Video = new VideoExportSettings { Codec = ExportCodec.H264, EncoderId = "h264_mf" } };

        Assert.True(ExportHardwareTuning.UsesGpuEncoder(h264, Nvidia));
        Assert.False(ExportHardwareTuning.UsesGpuEncoder(av1, Nvidia));
        Assert.True(ExportHardwareTuning.UsesGpuEncoder(av1, Intel));
        Assert.False(ExportHardwareTuning.UsesGpuEncoder(prores, Amd));
        Assert.False(ExportHardwareTuning.UsesGpuEncoder(forcedCpu, Nvidia));
        Assert.False(ExportHardwareTuning.UsesGpuEncoder(h264, NoGpu));
    }

    private static string ExportPlanChoose(ExportCodec codec, IReadOnlyList<EncoderSpec> available) =>
        Rodaje.Export.Running.ExportPlan.Choose(codec, null, available).Id;

    private static GpuProfile Profile(params GpuAdapter[] adapters) => new(adapters, HardwareDecodeProfiles.None, 16);
}
