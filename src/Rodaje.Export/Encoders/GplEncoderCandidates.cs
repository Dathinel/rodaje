using Rodaje.Media.Export;

namespace Rodaje.Export.Encoders;

/// <summary>
/// x264 y x265: solo existen en el <c>ffmpeg.exe</c> GPL del complemento <c>rodaje-codificadores-gpl</c>, que se ejecuta
/// como proceso aparte por la misma tubería que el LGPL. Nunca entran en una elección automática ni en un preajuste:
/// codifican H.264/HEVC en software, sin la cobertura de patentes de la GPU o de Windows (<c>docs/legal.md</c>).
/// </summary>
public static class GplEncoderCandidates
{
    public const string Note = "Complemento GPL (codificadores GPL). Patentes de H.264/HEVC sin cubrir por software: no por defecto.";

    private static readonly string[] X26xPresets = ["ultrafast", "superfast", "veryfast", "faster", "fast", "medium", "slow", "slower", "veryslow", "placebo"];

    public static EncoderSpec X264 { get; } = new()
    {
        Id = "libx264", Codec = ExportCodec.H264, IsHardware = false, Vendor = EncoderVendor.Software, License = EncoderLicense.GplAddOn,
        InDefaults = false, Note = Note,
        PixelFormats = [ExportPixelFormat.Yuv420P, ExportPixelFormat.Yuv420P10, ExportPixelFormat.Yuv422P, ExportPixelFormat.Yuv444P],
        Profiles =
        [
            new("high", "High") { PixelFormats = [ExportPixelFormat.Yuv420P] },
            new("main", "Main") { PixelFormats = [ExportPixelFormat.Yuv420P] },
            new("high10", "High 10") { PixelFormats = [ExportPixelFormat.Yuv420P10] },
            new("high422", "High 4:2:2") { PixelFormats = [ExportPixelFormat.Yuv422P] },
            new("high444", "High 4:4:4") { PixelFormats = [ExportPixelFormat.Yuv444P] },
        ],
        Levels = ["3.1", "4.0", "4.1", "4.2", "5.0", "5.1", "5.2"],
        RateControls = [RateControlMode.ConstantQuality, RateControlMode.Vbr, RateControlMode.Cbr, RateControlMode.Abr],
        Quality = new("CRF", "-crf", 0, 51, 20),
        SupportsTwoPass = true, MaxBFrames = 16,
        SpeedPresets = X26xPresets, DefaultSpeedPreset = "medium",
        Tunes = ["film", "animation", "grain", "stillimage", "fastdecode", "zerolatency"],
    };

    public static EncoderSpec X265 { get; } = new()
    {
        Id = "libx265", Codec = ExportCodec.Hevc, IsHardware = false, Vendor = EncoderVendor.Software, License = EncoderLicense.GplAddOn,
        InDefaults = false, Note = Note,
        PixelFormats = [ExportPixelFormat.Yuv420P, ExportPixelFormat.Yuv420P10, ExportPixelFormat.Yuv444P10],
        Profiles =
        [
            new("main", "Main") { PixelFormats = [ExportPixelFormat.Yuv420P] },
            new("main10", "Main 10") { PixelFormats = [ExportPixelFormat.Yuv420P10] },
            new("main444-10", "Main 4:4:4 10") { PixelFormats = [ExportPixelFormat.Yuv444P10] },
        ],
        RateControls = [RateControlMode.ConstantQuality, RateControlMode.Vbr, RateControlMode.Abr],
        Quality = new("CRF", "-crf", 0, 51, 24),
        MaxBFrames = 16,
        SpeedPresets = X26xPresets, DefaultSpeedPreset = "medium",
        Tunes = ["grain", "animation", "fastdecode", "zerolatency"],
    };

    public static IReadOnlyList<EncoderSpec> All { get; } = [X264, X265];
}

/// <summary>Los codificadores de varias fuentes, en su orden (primero los LGPL, luego los del complemento GPL).</summary>
public sealed class CombinedEncoderSource(params IExportEncoderSource[] sources) : IExportEncoderSource
{
    public async Task<IReadOnlyList<EncoderSpec>> GetAsync(CancellationToken cancellationToken = default)
    {
        var all = new List<EncoderSpec>();
        foreach (IExportEncoderSource source in sources)
        {
            all.AddRange(await source.GetAsync(cancellationToken).ConfigureAwait(false));
        }

        return all;
    }
}
