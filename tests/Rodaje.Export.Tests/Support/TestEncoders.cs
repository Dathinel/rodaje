using Rodaje.Media.Export;

namespace Rodaje.Export.Tests.Support;

/// <summary>Dobles de <see cref="EncoderSpec"/> con capacidades realistas; el catálogo de verdad es de la tarea 13 de la nube.</summary>
internal static class TestEncoders
{
    public static EncoderSpec H264Nvenc { get; } = new()
    {
        Id = "h264_nvenc", Codec = ExportCodec.H264, IsHardware = true, Vendor = EncoderVendor.Nvidia, License = EncoderLicense.SystemOrDriver,
        PixelFormats = [ExportPixelFormat.Nv12, ExportPixelFormat.Yuv444P],
        Profiles = [new("high", "High"), new("main", "Main"), new("high444p", "High 4:4:4") { PixelFormats = [ExportPixelFormat.Yuv444P] }],
        Levels = ["4.1", "5.1"],
        RateControls = [RateControlMode.Vbr, RateControlMode.ConstantQuality, RateControlMode.Cbr],
        Quality = new("CQ", "-cq", 0, 51, 23), SupportsTwoPass = true, MaxBFrames = 4,
        SpeedPresets = ["p1", "p4", "p7"], DefaultSpeedPreset = "p4",
    };

    public static EncoderSpec HevcNvenc { get; } = H264Nvenc with
    {
        Id = "hevc_nvenc", Codec = ExportCodec.Hevc, PixelFormats = [ExportPixelFormat.Nv12, ExportPixelFormat.P010],
        Profiles = [new("main", "Main") { PixelFormats = [ExportPixelFormat.Nv12] }, new("main10", "Main 10") { PixelFormats = [ExportPixelFormat.P010] }],
        Levels = [],
    };

    public static EncoderSpec SvtAv1 { get; } = new()
    {
        Id = "libsvtav1", Codec = ExportCodec.Av1, IsHardware = false, Vendor = EncoderVendor.Software, License = EncoderLicense.Permissive,
        PixelFormats = [ExportPixelFormat.Yuv420P, ExportPixelFormat.Yuv420P10],
        RateControls = [RateControlMode.ConstantQuality, RateControlMode.Vbr], Quality = new("CRF", "-crf", 1, 63, 35), MaxBFrames = 0,
    };

    public static EncoderSpec Kvazaar { get; } = new()
    {
        Id = "libkvazaar", Codec = ExportCodec.Hevc, IsHardware = false, Vendor = EncoderVendor.Software, License = EncoderLicense.Permissive,
        InDefaults = false, Note = "Patentes de HEVC sin cubrir: no por defecto.", PixelFormats = [ExportPixelFormat.Yuv420P],
        RateControls = [RateControlMode.ConstantQuality],
    };

    public static EncoderSpec ProRes { get; } = new()
    {
        Id = "prores_ks", Codec = ExportCodec.ProRes, IsHardware = false, Vendor = EncoderVendor.Software, License = EncoderLicense.FfmpegLgpl,
        PixelFormats = [ExportPixelFormat.Yuv422P10, ExportPixelFormat.Yuv444P10, ExportPixelFormat.Yuva444P10],
        Profiles = [new("hq", "422 HQ") { PixelFormats = [ExportPixelFormat.Yuv422P10] }, new("4444", "4444") { PixelFormats = [ExportPixelFormat.Yuv444P10, ExportPixelFormat.Yuva444P10] }],
        RateControls = [RateControlMode.ProfileDefined], IntraOnly = true,
    };

    public static EncoderSpec Gif { get; } = new()
    {
        Id = "gif", Codec = ExportCodec.Gif, IsHardware = false, Vendor = EncoderVendor.Software, License = EncoderLicense.FfmpegLgpl,
        PixelFormats = [ExportPixelFormat.Rgb24], RateControls = [RateControlMode.ProfileDefined],
    };

    public static EncoderSpec AacMf { get; } = new()
    {
        Id = "aac_mf", Codec = ExportCodec.Aac, IsHardware = false, Vendor = EncoderVendor.Microsoft, License = EncoderLicense.SystemOrDriver,
        RateControls = [RateControlMode.Cbr], SampleRates = [44_100, 48_000], MaxChannels = 6,
    };

    public static EncoderSpec Opus { get; } = new()
    {
        Id = "libopus", Codec = ExportCodec.Opus, IsHardware = false, Vendor = EncoderVendor.Software, License = EncoderLicense.Permissive,
        RateControls = [RateControlMode.Vbr, RateControlMode.Cbr], SampleRates = [48_000], MaxChannels = 8,
    };

    public static EncoderSpec Pcm24 { get; } = new()
    {
        Id = "pcm_s24le", Codec = ExportCodec.Pcm, IsHardware = false, Vendor = EncoderVendor.Software, License = EncoderLicense.FfmpegLgpl,
        RateControls = [RateControlMode.Lossless], MaxChannels = 8,
    };

    public static EncoderSpec For(ExportCodec codec) => codec switch
    {
        ExportCodec.H264 => H264Nvenc,
        ExportCodec.Hevc => HevcNvenc,
        ExportCodec.Av1 => SvtAv1,
        ExportCodec.ProRes => ProRes,
        ExportCodec.Gif => Gif,
        ExportCodec.Aac => AacMf,
        ExportCodec.Opus => Opus,
        ExportCodec.Pcm => Pcm24,
        _ => throw new ArgumentOutOfRangeException(nameof(codec), codec, "Sin doble para este códec."),
    };
}
