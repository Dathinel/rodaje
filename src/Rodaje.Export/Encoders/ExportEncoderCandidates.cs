using Rodaje.Media.Export;

namespace Rodaje.Export.Encoders;

/// <summary>
/// Los codificadores que se prueban con la sonda, con sus capacidades, en orden de preferencia (hardware primero; en AAC,
/// Media Foundation antes que el nativo). Provisional hasta que se una el catálogo de la tarea 13 de la nube.
/// </summary>
public static class ExportEncoderCandidates
{
    private static readonly QualityScale NvencCq = new("CQ", "-cq", 0, 51, 23);

    private static readonly string[] NvencPresets = ["p1", "p2", "p3", "p4", "p5", "p6", "p7"];

    private static readonly RateControlMode[] GpuModes = [RateControlMode.ConstantQuality, RateControlMode.Vbr, RateControlMode.Cbr];

    private static readonly int[] AudioRates = [44_100, 48_000, 96_000];

    public static EncoderSpec H264Nvenc { get; } = new()
    {
        Id = "h264_nvenc", Codec = ExportCodec.H264, IsHardware = true, Vendor = EncoderVendor.Nvidia, License = EncoderLicense.SystemOrDriver,
        PixelFormats = [ExportPixelFormat.Nv12],
        Profiles = [new("high", "High"), new("main", "Main"), new("baseline", "Baseline")],
        Levels = ["4.0", "4.1", "4.2", "5.0", "5.1", "5.2"],
        RateControls = GpuModes, Quality = NvencCq, SupportsTwoPass = true, MaxBFrames = 4,
        SpeedPresets = NvencPresets, DefaultSpeedPreset = "p4", Tunes = ["hq", "ll"],
    };

    public static EncoderSpec HevcNvenc { get; } = new()
    {
        Id = "hevc_nvenc", Codec = ExportCodec.Hevc, IsHardware = true, Vendor = EncoderVendor.Nvidia, License = EncoderLicense.SystemOrDriver,
        PixelFormats = [ExportPixelFormat.Nv12, ExportPixelFormat.P010],
        Profiles = [new("main", "Main") { PixelFormats = [ExportPixelFormat.Nv12] }, new("main10", "Main 10") { PixelFormats = [ExportPixelFormat.P010] }],
        RateControls = GpuModes, Quality = NvencCq, SupportsTwoPass = true, MaxBFrames = 4,
        SpeedPresets = NvencPresets, DefaultSpeedPreset = "p4", Tunes = ["hq", "ll"],
    };

    public static EncoderSpec H264Mf { get; } = new()
    {
        Id = "h264_mf", Codec = ExportCodec.H264, IsHardware = false, Vendor = EncoderVendor.Microsoft, License = EncoderLicense.SystemOrDriver,
        PixelFormats = [ExportPixelFormat.Nv12], Profiles = [new("high", "High"), new("main", "Main"), new("baseline", "Baseline")],
        RateControls = [RateControlMode.Vbr, RateControlMode.Cbr, RateControlMode.ConstantQuality], Quality = new("q", "-q:v", 1, 100, 60, LowerIsBetter: false),
        MaxBFrames = 2,
    };

    public static EncoderSpec SvtAv1 { get; } = new()
    {
        Id = "libsvtav1", Codec = ExportCodec.Av1, IsHardware = false, Vendor = EncoderVendor.Software, License = EncoderLicense.Permissive,
        PixelFormats = [ExportPixelFormat.Yuv420P, ExportPixelFormat.Yuv420P10],
        Profiles = [new("main", "Main")], RateControls = [RateControlMode.ConstantQuality, RateControlMode.Vbr, RateControlMode.Abr],
        Quality = new("CRF", "-crf", 1, 63, 35), SupportsTwoPass = true,
        SpeedPresets = ["12", "10", "8", "6", "4"], DefaultSpeedPreset = "8",
    };

    public static EncoderSpec Vp9 { get; } = new()
    {
        Id = "libvpx-vp9", Codec = ExportCodec.Vp9, IsHardware = false, Vendor = EncoderVendor.Software, License = EncoderLicense.Permissive,
        PixelFormats = [ExportPixelFormat.Yuv420P, ExportPixelFormat.Yuv420P10, ExportPixelFormat.Yuva420P],
        RateControls = [RateControlMode.ConstantQuality, RateControlMode.Vbr, RateControlMode.Cbr, RateControlMode.Abr],
        Quality = new("CRF", "-crf", 0, 63, 31), SupportsTwoPass = true,
    };

    public static EncoderSpec ProRes { get; } = new()
    {
        Id = "prores_ks", Codec = ExportCodec.ProRes, IsHardware = false, Vendor = EncoderVendor.Software, License = EncoderLicense.FfmpegLgpl,
        PixelFormats = [ExportPixelFormat.Yuv422P10, ExportPixelFormat.Yuv444P10, ExportPixelFormat.Yuva444P10],
        Profiles =
        [
            new("proxy", "ProRes 422 Proxy") { PixelFormats = [ExportPixelFormat.Yuv422P10] },
            new("lt", "ProRes 422 LT") { PixelFormats = [ExportPixelFormat.Yuv422P10] },
            new("standard", "ProRes 422") { PixelFormats = [ExportPixelFormat.Yuv422P10] },
            new("hq", "ProRes 422 HQ") { PixelFormats = [ExportPixelFormat.Yuv422P10] },
            new("4444", "ProRes 4444") { PixelFormats = [ExportPixelFormat.Yuv444P10, ExportPixelFormat.Yuva444P10] },
        ],
        RateControls = [RateControlMode.ProfileDefined], IntraOnly = true,
    };

    public static EncoderSpec DnxHr { get; } = new()
    {
        Id = "dnxhd", Codec = ExportCodec.DnxHr, IsHardware = false, Vendor = EncoderVendor.Software, License = EncoderLicense.FfmpegLgpl,
        PixelFormats = [ExportPixelFormat.Yuv422P, ExportPixelFormat.Yuv422P10, ExportPixelFormat.Yuv444P10],
        Profiles =
        [
            new("dnxhr_sq", "DNxHR SQ") { PixelFormats = [ExportPixelFormat.Yuv422P] },
            new("dnxhr_lb", "DNxHR LB") { PixelFormats = [ExportPixelFormat.Yuv422P] },
            new("dnxhr_hq", "DNxHR HQ") { PixelFormats = [ExportPixelFormat.Yuv422P] },
            new("dnxhr_hqx", "DNxHR HQX") { PixelFormats = [ExportPixelFormat.Yuv422P10] },
            new("dnxhr_444", "DNxHR 444") { PixelFormats = [ExportPixelFormat.Yuv444P10] },
        ],
        RateControls = [RateControlMode.ProfileDefined], IntraOnly = true,
    };

    public static EncoderSpec Ffv1 { get; } = new()
    {
        Id = "ffv1", Codec = ExportCodec.Ffv1, IsHardware = false, Vendor = EncoderVendor.Software, License = EncoderLicense.FfmpegLgpl,
        PixelFormats = [ExportPixelFormat.Yuv420P, ExportPixelFormat.Yuv420P10, ExportPixelFormat.Yuv422P10, ExportPixelFormat.Yuv444P10],
        RateControls = [RateControlMode.Lossless], IntraOnly = true,
    };

    public static EncoderSpec Gif { get; } = new()
    {
        Id = "gif", Codec = ExportCodec.Gif, IsHardware = false, Vendor = EncoderVendor.Software, License = EncoderLicense.FfmpegLgpl,
        PixelFormats = [ExportPixelFormat.Rgb24], RateControls = [RateControlMode.ProfileDefined],
    };

    public static EncoderSpec Png { get; } = new()
    {
        Id = "png", Codec = ExportCodec.Png, IsHardware = false, Vendor = EncoderVendor.Software, License = EncoderLicense.FfmpegLgpl,
        PixelFormats = [ExportPixelFormat.Rgb24, ExportPixelFormat.Rgba, ExportPixelFormat.Rgba64],
        RateControls = [RateControlMode.Lossless], IntraOnly = true,
    };

    public static EncoderSpec AacMf { get; } = Audio("aac_mf", ExportCodec.Aac, EncoderVendor.Microsoft, EncoderLicense.SystemOrDriver,
        [RateControlMode.Cbr], [44_100, 48_000], 6);

    public static EncoderSpec Aac { get; } = Audio("aac", ExportCodec.Aac, EncoderVendor.Software, EncoderLicense.FfmpegLgpl,
        [RateControlMode.Cbr, RateControlMode.Vbr], AudioRates, 6);

    public static EncoderSpec Opus { get; } = Audio("libopus", ExportCodec.Opus, EncoderVendor.Software, EncoderLicense.Permissive,
        [RateControlMode.Vbr, RateControlMode.Cbr], [48_000], 6);

    public static EncoderSpec Mp3 { get; } = Audio("libmp3lame", ExportCodec.Mp3, EncoderVendor.Software, EncoderLicense.Lgpl,
        [RateControlMode.Cbr, RateControlMode.Vbr], [44_100, 48_000], 2);

    public static EncoderSpec Flac { get; } = Audio("flac", ExportCodec.Flac, EncoderVendor.Software, EncoderLicense.FfmpegLgpl,
        [RateControlMode.Lossless], AudioRates, 6);

    public static EncoderSpec Pcm { get; } = Audio("pcm_s24le", ExportCodec.Pcm, EncoderVendor.Software, EncoderLicense.FfmpegLgpl,
        [RateControlMode.Lossless], AudioRates, 6);

    /// <summary>En orden de preferencia: hardware primero.</summary>
    public static IReadOnlyList<EncoderSpec> All { get; } =
        [H264Nvenc, HevcNvenc, H264Mf, SvtAv1, Vp9, ProRes, DnxHr, Ffv1, Gif, Png, AacMf, Aac, Opus, Mp3, Flac, Pcm];

    private static EncoderSpec Audio(string id, ExportCodec codec, EncoderVendor vendor, EncoderLicense license,
        RateControlMode[] modes, int[] rates, int channels) => new()
        {
            Id = id, Codec = codec, IsHardware = false, Vendor = vendor, License = license, RateControls = modes,
            SampleRates = rates, MaxChannels = channels,
        };
}
