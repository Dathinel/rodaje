namespace Rodaje.Media.Export;

/// <summary>
/// Catálogo de codificadores de salida (<see cref="EncoderSpec"/>) en orden de preferencia: primero la GPU (NVIDIA, AMD,
/// Intel), luego Media Foundation de Windows, luego el software LGPL sin patentes de consorcio (AV1, VP9), luego kvazaar y
/// OpenH264 (solo a mano), los maestros e imágenes, y al final el audio. Solo hay lo que lleva la compilación LGPL de FFmpeg
/// (<c>docs/ffmpeg-configure.txt</c>); x264 y x265 (GPL) no existen aquí ni deben añadirse.
/// </summary>
/// <remarks>
/// El catálogo solo describe: los argumentos de calidad, tasa, perfil, preset y GOP los arma <c>FfmpegArguments</c> en
/// <c>Rodaje.Export</c> a partir de estas capacidades. <see cref="EncoderSpec.BaseArguments"/> lleva solo lo fijo que esa
/// clase no pone (velocidad de libaom y libvpx, preajuste de kvazaar, modo de OpenH264…). Qué funciona en el equipo lo
/// dice <see cref="ExportEncoderAvailability"/>.
/// </remarks>
public static class ExportEncoderCatalog
{
    private const string SoftwareH26xNote = "Patentes de H.264/HEVC sin cubrir por software: no por defecto.";

    // AMD e Intel: capacidades y argumentos sacados de la documentación de FFmpeg 9, sin un equipo con que probarlos.
    private const string UntestedAmdNote = "Sin probar en un equipo AMD; la sonda decide si se ofrece.";
    private const string UntestedIntelNote = "Sin probar en un equipo Intel; la sonda decide si se ofrece.";

    private static readonly IReadOnlyList<RateControlMode> GpuRates =
        [RateControlMode.ConstantQuality, RateControlMode.Vbr, RateControlMode.Cbr, RateControlMode.Abr];

    private static readonly IReadOnlyList<string> NvencPresets = ["p1", "p2", "p3", "p4", "p5", "p6", "p7"];

    private static readonly IReadOnlyList<string> QsvPresets = ["veryfast", "faster", "fast", "medium", "slow", "slower", "veryslow"];

    private static readonly IReadOnlyList<string> H264Levels = ["3.1", "4.0", "4.1", "4.2", "5.0", "5.1", "5.2", "6.0", "6.1", "6.2"];

    private static readonly IReadOnlyList<string> HevcLevels = ["3.1", "4.0", "4.1", "5.0", "5.1", "5.2", "6.0", "6.1", "6.2"];

    private static readonly IReadOnlyList<int> AacRates =
        [8_000, 11_025, 12_000, 16_000, 22_050, 24_000, 32_000, 44_100, 48_000, 64_000, 88_200, 96_000];

    // ---- GPU ----

    private static EncoderSpec NvencH264 { get; } = new()
    {
        Id = "h264_nvenc", Codec = ExportCodec.H264, IsHardware = true, Vendor = EncoderVendor.Nvidia, License = EncoderLicense.SystemOrDriver,
        PixelFormats = [ExportPixelFormat.Nv12, ExportPixelFormat.Yuv444P],
        Profiles =
        [
            new("high", "High") { PixelFormats = [ExportPixelFormat.Nv12] },
            new("main", "Main") { PixelFormats = [ExportPixelFormat.Nv12] },
            new("high444p", "High 4:4:4") { PixelFormats = [ExportPixelFormat.Yuv444P] },
        ],
        Levels = H264Levels,
        RateControls = [.. GpuRates, RateControlMode.Lossless],
        Quality = new("CQ", "-cq", 1, 51, 23),
        SupportsTwoPass = true, MaxBFrames = 4,
        SpeedPresets = NvencPresets, DefaultSpeedPreset = "p5",
        Tunes = ["hq", "ll", "ull"],
    };

    private static EncoderSpec NvencHevc { get; } = NvencH264 with
    {
        Id = "hevc_nvenc", Codec = ExportCodec.Hevc,
        PixelFormats = [ExportPixelFormat.Nv12, ExportPixelFormat.P010, ExportPixelFormat.Yuv444P],
        Profiles =
        [
            new("main", "Main") { PixelFormats = [ExportPixelFormat.Nv12] },
            new("main10", "Main 10") { PixelFormats = [ExportPixelFormat.P010] },
            new("rext", "Main 4:4:4 (RExt)") { PixelFormats = [ExportPixelFormat.Yuv444P] },
        ],
        Levels = HevcLevels,
        Tunes = ["hq", "uhq", "ll", "ull"],
    };

    private static EncoderSpec NvencAv1 { get; } = NvencH264 with
    {
        Id = "av1_nvenc", Codec = ExportCodec.Av1,
        Note = "Solo RTX 40 (Ada) o posterior.",
        PixelFormats = [ExportPixelFormat.Nv12, ExportPixelFormat.P010],
        // AV1 solo tiene el perfil Main en la GPU: sin -profile:v (av1_nvenc no admite «main» como valor).
        Profiles = [],
        Levels = [],
        RateControls = GpuRates,
        Quality = new("CQ", "-cq", 1, 51, 30),
        Tunes = ["hq", "uhq", "ll", "ull"],
    };

    private static EncoderSpec AmfH264 { get; } = new()
    {
        Id = "h264_amf", Codec = ExportCodec.H264, IsHardware = true, Vendor = EncoderVendor.Amd, License = EncoderLicense.SystemOrDriver,
        Note = UntestedAmdNote,
        PixelFormats = [ExportPixelFormat.Nv12],
        Profiles = [new("high", "High"), new("main", "Main"), new("constrained_baseline", "Constrained Baseline")],
        Levels = H264Levels,
        RateControls = [RateControlMode.ConstantQuality, RateControlMode.Vbr, RateControlMode.Cbr],
        Quality = new("QVBR", "-qvbr_quality_level", 1, 51, 23),
        MaxBFrames = 3,
        SpeedPresets = ["speed", "balanced", "quality"], DefaultSpeedPreset = "balanced",
    };

    private static EncoderSpec AmfHevc { get; } = AmfH264 with
    {
        Id = "hevc_amf", Codec = ExportCodec.Hevc,
        PixelFormats = [ExportPixelFormat.Nv12, ExportPixelFormat.P010],
        Profiles =
        [
            new("main", "Main") { PixelFormats = [ExportPixelFormat.Nv12] },
            new("main10", "Main 10") { PixelFormats = [ExportPixelFormat.P010] },
        ],
        Levels = HevcLevels,
        MaxBFrames = 0,
    };

    private static EncoderSpec AmfAv1 { get; } = AmfHevc with
    {
        Id = "av1_amf", Codec = ExportCodec.Av1,
        Note = "Solo Radeon RX 7000 o posterior. " + UntestedAmdNote,
        Profiles = [],
        Levels = [],
        SpeedPresets = ["speed", "balanced", "quality", "high_quality"],
    };

    private static EncoderSpec QsvH264 { get; } = new()
    {
        Id = "h264_qsv", Codec = ExportCodec.H264, IsHardware = true, Vendor = EncoderVendor.Intel, License = EncoderLicense.SystemOrDriver,
        Note = UntestedIntelNote,
        PixelFormats = [ExportPixelFormat.Nv12],
        Profiles = [new("high", "High"), new("main", "Main"), new("baseline", "Baseline")],
        Levels = H264Levels,
        RateControls = [RateControlMode.ConstantQuality, RateControlMode.Vbr, RateControlMode.Cbr],
        Quality = new("ICQ", "-global_quality", 1, 51, 23),
        MaxBFrames = 3,
        SpeedPresets = QsvPresets, DefaultSpeedPreset = "medium",
    };

    private static EncoderSpec QsvHevc { get; } = QsvH264 with
    {
        Id = "hevc_qsv", Codec = ExportCodec.Hevc,
        PixelFormats = [ExportPixelFormat.Nv12, ExportPixelFormat.P010],
        Profiles =
        [
            new("main", "Main") { PixelFormats = [ExportPixelFormat.Nv12] },
            new("main10", "Main 10") { PixelFormats = [ExportPixelFormat.P010] },
        ],
        Levels = HevcLevels,
    };

    private static EncoderSpec QsvAv1 { get; } = QsvHevc with
    {
        Id = "av1_qsv", Codec = ExportCodec.Av1,
        Note = "Solo Intel Arc o Core Ultra o posterior. " + UntestedIntelNote,
        Profiles = [],
        Levels = [],
    };

    // ---- Media Foundation ----
    // «-hw_encoding 0» pide el MFT de software de Microsoft, que está en todo Windows con el Media Feature Pack (no en las
    // ediciones N sin él) y llega con la licencia de patentes de Windows. Con «1» Windows elegiría el MFT del controlador
    // de la GPU, que NVENC, AMF y QSV ya cubren antes en el catálogo. Así Media Foundation es el primer recurso sin GPU.

    private static EncoderSpec MfH264 { get; } = new()
    {
        Id = "h264_mf", Codec = ExportCodec.H264, IsHardware = false, Vendor = EncoderVendor.Microsoft, License = EncoderLicense.SystemOrDriver,
        Note = "Codificador de Windows por CPU; falta en las ediciones N sin el Media Feature Pack.",
        PixelFormats = [ExportPixelFormat.Nv12],
        BaseArguments = ["-hw_encoding", "0"],
        RateControls = [RateControlMode.ConstantQuality, RateControlMode.Vbr, RateControlMode.Cbr, RateControlMode.Abr],
        Quality = new("Calidad", "-quality", 0, 100, 60, LowerIsBetter: false),
    };

    private static EncoderSpec MfHevc { get; } = MfH264 with
    {
        Id = "hevc_mf", Codec = ExportCodec.Hevc,
        Note = "Codificador de Windows por CPU; necesita las «Extensiones de video HEVC» de Microsoft Store.",
    };

    // ---- Software sin patentes de consorcio ----

    private static EncoderSpec SvtAv1 { get; } = new()
    {
        Id = "libsvtav1", Codec = ExportCodec.Av1, IsHardware = false, Vendor = EncoderVendor.Software, License = EncoderLicense.Permissive,
        PixelFormats = [ExportPixelFormat.Yuv420P, ExportPixelFormat.Yuv420P10],
        RateControls = [RateControlMode.ConstantQuality, RateControlMode.Vbr, RateControlMode.Cbr],
        Quality = new("CRF", "-crf", 1, 63, 35),
        // -preset de SVT-AV1: 13 es el más rápido y 0 el más lento.
        SpeedPresets = ["13", "12", "11", "10", "9", "8", "7", "6", "5", "4", "3", "2", "1", "0"], DefaultSpeedPreset = "8",
        // tune de SVT-AV1 (FfmpegArguments lo pasa en -svtav1-params): 0 psicovisual, 1 PSNR, 2 SSIM.
        Tunes = ["0", "1", "2"],
    };

    private static EncoderSpec Aom { get; } = new()
    {
        Id = "libaom-av1", Codec = ExportCodec.Av1, IsHardware = false, Vendor = EncoderVendor.Software, License = EncoderLicense.Permissive,
        Note = "Más lento que SVT-AV1; queda por si SVT-AV1 falla.",
        PixelFormats = [ExportPixelFormat.Yuv420P, ExportPixelFormat.Yuv420P10, ExportPixelFormat.Yuv444P, ExportPixelFormat.Yuv444P10],
        // libaom no tiene -preset: la velocidad va fija con -cpu-used (0 lento … 8 rápido).
        BaseArguments = ["-cpu-used", "6", "-row-mt", "1"],
        RateControls = [RateControlMode.ConstantQuality, RateControlMode.Vbr, RateControlMode.Cbr, RateControlMode.Abr, RateControlMode.Lossless],
        Quality = new("CRF", "-crf", 0, 63, 32),
        SupportsTwoPass = true,
    };

    private static EncoderSpec Vpx { get; } = new()
    {
        Id = "libvpx-vp9", Codec = ExportCodec.Vp9, IsHardware = false, Vendor = EncoderVendor.Software, License = EncoderLicense.Permissive,
        PixelFormats = [ExportPixelFormat.Yuv420P, ExportPixelFormat.Yuv420P10, ExportPixelFormat.Yuva420P],
        // libvpx no tiene -preset: modo «good» y -cpu-used fijo (0 lento … 5 rápido).
        BaseArguments = ["-deadline", "good", "-cpu-used", "2", "-row-mt", "1"],
        RateControls = [RateControlMode.ConstantQuality, RateControlMode.Vbr, RateControlMode.Cbr, RateControlMode.Abr, RateControlMode.Lossless],
        Quality = new("CRF", "-crf", 0, 63, 31),
        SupportsTwoPass = true,
    };

    // ---- Software con patentes de consorcio: solo si el usuario lo elige (D-028) ----

    private static EncoderSpec Kvazaar { get; } = new()
    {
        Id = "libkvazaar", Codec = ExportCodec.Hevc, IsHardware = false, Vendor = EncoderVendor.Software, License = EncoderLicense.Permissive,
        InDefaults = false, Note = SoftwareH26xNote,
        PixelFormats = [ExportPixelFormat.Yuv420P],
        // libkvazaar no tiene -crf ni -preset (todo va en -kvazaar-params) y FfmpegArguments pondría -crf con calidad
        // constante: por eso solo tasa media (-b:v) y el preajuste fijo.
        BaseArguments = ["-kvazaar-params", "preset=medium"],
        RateControls = [RateControlMode.Abr],
    };

    private static EncoderSpec OpenH264 { get; } = new()
    {
        Id = "libopenh264", Codec = ExportCodec.H264, IsHardware = false, Vendor = EncoderVendor.Software, License = EncoderLicense.Permissive,
        InDefaults = false, Note = SoftwareH26xNote + " Calidad baja: ignora la calidad y la velocidad; solo admite tasa de bits.",
        PixelFormats = [ExportPixelFormat.Yuv420P],
        BaseArguments = ["-rc_mode", "bitrate", "-allow_skip_frames", "0"],
        RateControls = [RateControlMode.Abr],
    };

    // ---- Maestros ----

    private static EncoderSpec ProRes { get; } = new()
    {
        Id = "prores_ks", Codec = ExportCodec.ProRes, IsHardware = false, Vendor = EncoderVendor.Software, License = EncoderLicense.FfmpegLgpl,
        PixelFormats = [ExportPixelFormat.Yuv422P10, ExportPixelFormat.Yuv444P10, ExportPixelFormat.Yuva444P10],
        Profiles =
        [
            new("hq", "ProRes 422 HQ") { PixelFormats = [ExportPixelFormat.Yuv422P10] },
            new("proxy", "ProRes 422 Proxy") { PixelFormats = [ExportPixelFormat.Yuv422P10] },
            new("lt", "ProRes 422 LT") { PixelFormats = [ExportPixelFormat.Yuv422P10] },
            new("standard", "ProRes 422") { PixelFormats = [ExportPixelFormat.Yuv422P10] },
            new("4444", "ProRes 4444") { PixelFormats = [ExportPixelFormat.Yuv444P10, ExportPixelFormat.Yuva444P10] },
            new("4444xq", "ProRes 4444 XQ") { PixelFormats = [ExportPixelFormat.Yuv444P10, ExportPixelFormat.Yuva444P10] },
        ],
        RateControls = [RateControlMode.ProfileDefined],
        IntraOnly = true,
    };

    private static EncoderSpec DnxHr { get; } = new()
    {
        Id = "dnxhd", Codec = ExportCodec.DnxHr, IsHardware = false, Vendor = EncoderVendor.Software, License = EncoderLicense.FfmpegLgpl,
        PixelFormats = [ExportPixelFormat.Yuv422P, ExportPixelFormat.Yuv422P10, ExportPixelFormat.Yuv444P10],
        Profiles =
        [
            new("dnxhr_hq", "DNxHR HQ") { PixelFormats = [ExportPixelFormat.Yuv422P] },
            new("dnxhr_lb", "DNxHR LB") { PixelFormats = [ExportPixelFormat.Yuv422P] },
            new("dnxhr_sq", "DNxHR SQ") { PixelFormats = [ExportPixelFormat.Yuv422P] },
            new("dnxhr_hqx", "DNxHR HQX") { PixelFormats = [ExportPixelFormat.Yuv422P10] },
            new("dnxhr_444", "DNxHR 444") { PixelFormats = [ExportPixelFormat.Yuv444P10] },
        ],
        RateControls = [RateControlMode.ProfileDefined],
        IntraOnly = true,
    };

    private static EncoderSpec CineForm { get; } = new()
    {
        Id = "cfhd", Codec = ExportCodec.CineForm, IsHardware = false, Vendor = EncoderVendor.Software, License = EncoderLicense.FfmpegLgpl,
        Note = "Solo si este FFmpeg lo trae.",
        PixelFormats = [ExportPixelFormat.Yuv422P10],
        RateControls = [RateControlMode.ProfileDefined],
        IntraOnly = true,
    };

    private static EncoderSpec Ffv1 { get; } = new()
    {
        Id = "ffv1", Codec = ExportCodec.Ffv1, IsHardware = false, Vendor = EncoderVendor.Software, License = EncoderLicense.FfmpegLgpl,
        PixelFormats =
        [
            ExportPixelFormat.Yuv420P, ExportPixelFormat.Yuv420P10, ExportPixelFormat.Yuv422P, ExportPixelFormat.Yuv422P10,
            ExportPixelFormat.Yuv444P, ExportPixelFormat.Yuv444P10, ExportPixelFormat.Yuva420P, ExportPixelFormat.Yuva444P10,
        ],
        // Versión 3 con CRC por porción: lo habitual para archivo. «-g 1»: cada cuadro es clave, así que se puede cortar o
        // buscar en cualquiera. Sin él FFmpeg usa su GOP por defecto (12), porque FfmpegArguments no pone «-g» a los
        // codificadores que el catálogo marca como solo intra.
        BaseArguments = ["-level", "3", "-slicecrc", "1", "-g", "1"],
        RateControls = [RateControlMode.Lossless],
        IntraOnly = true,
    };

    // ---- Intermedios, animaciones e imágenes ----

    private static EncoderSpec Mjpeg { get; } = new()
    {
        Id = "mjpeg", Codec = ExportCodec.Mjpeg, IsHardware = false, Vendor = EncoderVendor.Software, License = EncoderLicense.FfmpegLgpl,
        PixelFormats = [ExportPixelFormat.Yuv420P, ExportPixelFormat.Yuv422P, ExportPixelFormat.Yuv444P],
        // JPEG en rango limitado (D-028) no es estándar: sin esto FFmpeg no abre el codificador.
        BaseArguments = ["-strict", "unofficial"],
        RateControls = [RateControlMode.ConstantQuality],
        Quality = new("q", "-q:v", 2, 31, 3),
        IntraOnly = true,
    };

    private static EncoderSpec Png { get; } = new()
    {
        Id = "png", Codec = ExportCodec.Png, IsHardware = false, Vendor = EncoderVendor.Software, License = EncoderLicense.FfmpegLgpl,
        PixelFormats = [ExportPixelFormat.Rgb24, ExportPixelFormat.Rgba, ExportPixelFormat.Rgba64],
        RateControls = [RateControlMode.Lossless],
        IntraOnly = true,
    };

    private static EncoderSpec QtAnimation { get; } = new()
    {
        Id = "qtrle", Codec = ExportCodec.QtAnimation, IsHardware = false, Vendor = EncoderVendor.Software, License = EncoderLicense.FfmpegLgpl,
        PixelFormats = [ExportPixelFormat.Rgb24, ExportPixelFormat.Rgba],
        RateControls = [RateControlMode.Lossless],
    };

    private static EncoderSpec Gif { get; } = new()
    {
        Id = "gif", Codec = ExportCodec.Gif, IsHardware = false, Vendor = EncoderVendor.Software, License = EncoderLicense.FfmpegLgpl,
        PixelFormats = [ExportPixelFormat.Rgb24],
        RateControls = [RateControlMode.ProfileDefined],
    };

    private static EncoderSpec WebP { get; } = new()
    {
        Id = "libwebp", Codec = ExportCodec.WebP, IsHardware = false, Vendor = EncoderVendor.Software, License = EncoderLicense.Permissive,
        PixelFormats = [ExportPixelFormat.Rgb24, ExportPixelFormat.Rgba],
        RateControls = [RateControlMode.ConstantQuality, RateControlMode.Lossless],
        Quality = new("Calidad", "-quality", 0, 100, 75, LowerIsBetter: false),
    };

    private static EncoderSpec Tiff { get; } = new()
    {
        Id = "tiff", Codec = ExportCodec.Tiff, IsHardware = false, Vendor = EncoderVendor.Software, License = EncoderLicense.FfmpegLgpl,
        PixelFormats = [ExportPixelFormat.Rgb24, ExportPixelFormat.Rgba, ExportPixelFormat.Rgba64],
        BaseArguments = ["-compression_algo", "deflate"],
        RateControls = [RateControlMode.Lossless],
        IntraOnly = true,
    };

    private static EncoderSpec Exr { get; } = new()
    {
        Id = "exr", Codec = ExportCodec.Exr, IsHardware = false, Vendor = EncoderVendor.Software, License = EncoderLicense.FfmpegLgpl,
        PixelFormats = [ExportPixelFormat.Rgba64],
        RateControls = [RateControlMode.Lossless],
        IntraOnly = true,
    };

    // ---- Audio ----

    private static EncoderSpec AacMf { get; } = new()
    {
        Id = "aac_mf", Codec = ExportCodec.Aac, IsHardware = false, Vendor = EncoderVendor.Microsoft, License = EncoderLicense.SystemOrDriver,
        Note = "AAC de Windows: solo 96, 128, 160 o 192 kbps.",
        RateControls = [RateControlMode.Cbr],
        SampleRates = [44_100, 48_000], MaxChannels = 6,
    };

    private static EncoderSpec Aac { get; } = new()
    {
        Id = "aac", Codec = ExportCodec.Aac, IsHardware = false, Vendor = EncoderVendor.Software, License = EncoderLicense.FfmpegLgpl,
        Profiles = [new("aac_low", "AAC-LC")],
        // Con -b:a el AAC de FFmpeg va a tasa constante: es el respaldo del AAC de Windows con el mismo ajuste (CBR por
        // defecto, como piden los preajustes) cuando este no está (ediciones N).
        RateControls = [RateControlMode.Cbr, RateControlMode.Abr, RateControlMode.ConstantQuality],
        Quality = new("q", "-q:a", 0.1, 2, 1, LowerIsBetter: false),
        SampleRates = AacRates, MaxChannels = 8,
    };

    private static EncoderSpec Opus { get; } = new()
    {
        Id = "libopus", Codec = ExportCodec.Opus, IsHardware = false, Vendor = EncoderVendor.Software, License = EncoderLicense.Permissive,
        RateControls = [RateControlMode.Vbr, RateControlMode.Cbr, RateControlMode.Abr],
        SampleRates = [48_000, 24_000, 16_000, 12_000, 8_000], MaxChannels = 8,
    };

    private static EncoderSpec Mp3 { get; } = new()
    {
        Id = "libmp3lame", Codec = ExportCodec.Mp3, IsHardware = false, Vendor = EncoderVendor.Software, License = EncoderLicense.Lgpl,
        RateControls = [RateControlMode.Cbr, RateControlMode.ConstantQuality, RateControlMode.Abr],
        Quality = new("V", "-q:a", 0, 9, 4),
        SampleRates = [48_000, 44_100, 32_000, 24_000, 22_050, 16_000, 12_000, 11_025, 8_000], MaxChannels = 2,
    };

    private static EncoderSpec Flac { get; } = new()
    {
        Id = "flac", Codec = ExportCodec.Flac, IsHardware = false, Vendor = EncoderVendor.Software, License = EncoderLicense.FfmpegLgpl,
        RateControls = [RateControlMode.Lossless], MaxChannels = 8,
    };

    private static EncoderSpec Alac { get; } = new()
    {
        Id = "alac", Codec = ExportCodec.Alac, IsHardware = false, Vendor = EncoderVendor.Software, License = EncoderLicense.FfmpegLgpl,
        RateControls = [RateControlMode.Lossless], MaxChannels = 8,
    };

    /// <summary>Codificadores de video, del más al menos preferido.</summary>
    public static IReadOnlyList<EncoderSpec> Video { get; } =
    [
        NvencH264, NvencHevc, NvencAv1,
        AmfH264, AmfHevc, AmfAv1,
        QsvH264, QsvHevc, QsvAv1,
        MfH264, MfHevc,
        SvtAv1, Aom, Vpx,
        Kvazaar, OpenH264,
        ProRes, DnxHr, CineForm, Ffv1,
        Mjpeg, Png, QtAnimation, Gif, WebP, Tiff, Exr,
    ];

    /// <summary>Codificadores de audio, del más al menos preferido (el AAC de Windows antes que el de FFmpeg).</summary>
    public static IReadOnlyList<EncoderSpec> Audio { get; } =
    [
        AacMf, Aac, Opus, Mp3, Flac, Alac, Pcm("pcm_s16le"), Pcm("pcm_s24le"), Pcm("pcm_f32le"),
    ];

    /// <summary>Todo el catálogo: el video y luego el audio, cada parte en su orden de preferencia.</summary>
    public static IReadOnlyList<EncoderSpec> All { get; } = [.. Video, .. Audio];

    private static readonly Dictionary<string, string> Names = new(StringComparer.Ordinal)
    {
        ["h264_nvenc"] = "NVIDIA NVENC (H.264)",
        ["hevc_nvenc"] = "NVIDIA NVENC (HEVC)",
        ["av1_nvenc"] = "NVIDIA NVENC (AV1)",
        ["h264_amf"] = "AMD AMF (H.264)",
        ["hevc_amf"] = "AMD AMF (HEVC)",
        ["av1_amf"] = "AMD AMF (AV1)",
        ["h264_qsv"] = "Intel Quick Sync (H.264)",
        ["hevc_qsv"] = "Intel Quick Sync (HEVC)",
        ["av1_qsv"] = "Intel Quick Sync (AV1)",
        ["h264_mf"] = "Windows Media Foundation (H.264)",
        ["hevc_mf"] = "Windows Media Foundation (HEVC)",
        ["libsvtav1"] = "SVT-AV1 (AV1)",
        ["libaom-av1"] = "libaom (AV1)",
        ["libvpx-vp9"] = "libvpx (VP9)",
        ["libkvazaar"] = "kvazaar (HEVC)",
        ["libopenh264"] = "OpenH264 (H.264, calidad baja)",
        // Solo con el complemento rodaje-codificadores-gpl (Rodaje.Export.Encoders.GplEncoderCandidates); no están en el catálogo.
        ["libx264"] = "x264 (H.264, complemento GPL)",
        ["libx265"] = "x265 (HEVC, complemento GPL)",
        ["prores_ks"] = "Apple ProRes",
        ["dnxhd"] = "Avid DNxHR",
        ["cfhd"] = "GoPro CineForm",
        ["ffv1"] = "FFV1 (sin pérdidas)",
        ["mjpeg"] = "Motion JPEG",
        ["png"] = "PNG",
        ["qtrle"] = "QuickTime Animation",
        ["gif"] = "GIF",
        ["libwebp"] = "WebP",
        ["tiff"] = "TIFF",
        ["exr"] = "OpenEXR",
        ["aac_mf"] = "AAC de Windows",
        ["aac"] = "AAC de FFmpeg",
        ["libopus"] = "Opus",
        ["libmp3lame"] = "MP3 (LAME)",
        ["flac"] = "FLAC",
        ["alac"] = "Apple Lossless (ALAC)",
        ["pcm_s16le"] = "PCM de 16 bits",
        ["pcm_s24le"] = "PCM de 24 bits",
        ["pcm_f32le"] = "PCM float de 32 bits",
    };

    /// <summary>El codificador con ese <see cref="EncoderSpec.Id"/>, o <c>null</c> si no está en el catálogo.</summary>
    public static EncoderSpec? Find(string id) =>
        All.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.Ordinal));

    /// <summary>Nombre que ve el usuario; el <see cref="EncoderSpec.Id"/> si no está en el catálogo.</summary>
    public static string DisplayName(EncoderSpec encoder)
    {
        ArgumentNullException.ThrowIfNull(encoder);
        return Names.TryGetValue(encoder.Id, out string? name) ? name : encoder.Id;
    }

    /// <summary>Posición en el catálogo, para ordenar por preferencia; <see cref="int.MaxValue"/> si no está.</summary>
    public static int IndexOf(string id)
    {
        for (int i = 0; i < All.Count; i++)
        {
            if (string.Equals(All[i].Id, id, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return int.MaxValue;
    }

    // PCM: el Id decide el formato de las muestras (16 o 24 bits enteros, o float de 32).
    private static EncoderSpec Pcm(string id) => new()
    {
        Id = id, Codec = ExportCodec.Pcm, IsHardware = false, Vendor = EncoderVendor.Software, License = EncoderLicense.FfmpegLgpl,
        RateControls = [RateControlMode.Lossless], MaxChannels = 8,
    };
}
