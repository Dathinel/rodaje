using Rodaje.Core.Timing;
using Rodaje.Media.Export;

namespace Rodaje.Export;

/// <summary>De dónde sale un preajuste.</summary>
public enum ExportPresetOrigin
{
    /// <summary>Viene con Rodaje.</summary>
    Factory,

    /// <summary>Lo guardó el usuario (o lo importó de un archivo).</summary>
    User,

    /// <summary>Viene en un <c>.paquete</c> instalado.</summary>
    Package,
}

/// <summary>Grupo de tarjetas del diálogo.</summary>
public enum ExportPresetCategory
{
    Platform,
    Quality,
    Master,
    Web,
    AudioOnly,
    Custom,
}

/// <summary>Un preajuste: unos ajustes con nombre. Es solo datos (se guardan y se importan como archivo).</summary>
/// <param name="Id">Estable: <c>rodaje.youtube-1080p</c> en los de fábrica, <c>usuario.*</c> o <c>paquete@versión/elemento</c> en los demás.</param>
public sealed record ExportPreset(string Id, string Name, string Description, ExportPresetCategory Category, ExportPresetOrigin Origin,
    ExportSettings Settings)
{
    /// <summary>Referencia del paquete del que viene, en los de <see cref="ExportPresetOrigin.Package"/>.</summary>
    public string? PackageReference { get; init; }
}

/// <summary>
/// Los preajustes de fábrica, calibrados con VMAF en la aceptación de la fase 6.
/// </summary>
public static class ExportPresets
{
    private static readonly AudioExportSettings Aac192 = new() { Codec = ExportCodec.Aac };

    public static ExportPreset YouTube1080p { get; } = Factory("rodaje.youtube-1080p", "YouTube 1080p",
        "H.264 por GPU, 1080p a la tasa de la secuencia, calidad alta.", ExportPresetCategory.Platform,
        new ExportSettings
        {
            Video = new VideoExportSettings
            {
                Codec = ExportCodec.H264, Width = 1920, Height = 1080, RateControl = RateControl.Vbr(12_000, 18_000),
                Profile = "high",
            },
            Audio = Aac192 with { Loudness = LoudnessTarget.Platforms },
        });

    public static ExportPreset YouTube4K { get; } = Factory("rodaje.youtube-4k", "YouTube 4K",
        "HEVC por GPU, 2160p, calidad alta.", ExportPresetCategory.Platform,
        new ExportSettings
        {
            Video = new VideoExportSettings
            {
                Codec = ExportCodec.Hevc, Width = 3840, Height = 2160, RateControl = RateControl.Vbr(45_000, 68_000),
                Profile = "main",
            },
            Audio = Aac192 with { Loudness = LoudnessTarget.Platforms },
        });

    public static ExportPreset Vertical { get; } = Factory("rodaje.vertical-1080x1920", "Reels / TikTok 9:16",
        "H.264 para redes, 1080 de ancho. Pon el proyecto en 9:16.", ExportPresetCategory.Platform,
        new ExportSettings
        {
            Video = new VideoExportSettings
            {
                Codec = ExportCodec.H264, Width = 1080, Height = 1920, Fit = ExportFit.Bars, RateControl = RateControl.Vbr(10_000, 15_000),
                Profile = "high", FrameRate = Rational.Fps30,
            },
            Audio = Aac192 with { Loudness = LoudnessTarget.Platforms },
        });

    public static ExportPreset Square { get; } = Factory("rodaje.cuadrado-1080x1080", "Instagram 1:1",
        "H.264 para redes, 1080 de lado. Pon el proyecto en 1:1.", ExportPresetCategory.Platform,
        new ExportSettings
        {
            Video = new VideoExportSettings
            {
                Codec = ExportCodec.H264, Width = 1080, Height = 1080, Fit = ExportFit.Bars, RateControl = RateControl.Vbr(8_000, 12_000),
                Profile = "high",
            },
            Audio = Aac192 with { Loudness = LoudnessTarget.Platforms },
        });

    public static ExportPreset BestQuality { get; } = Factory("rodaje.maxima-calidad", "Máxima calidad",
        "HEVC de 10 bits por GPU, calidad casi sin pérdidas.", ExportPresetCategory.Quality,
        new ExportSettings
        {
            Video = new VideoExportSettings
            {
                Codec = ExportCodec.Hevc, BitDepth = 10, Profile = "main10", RateControl = RateControl.ConstantQuality(18),
            },
            Audio = Aac192 with { RateControl = new() { Mode = RateControlMode.Cbr, BitrateKbps = 320 } },
        });

    public static ExportPreset SmallFile { get; } = Factory("rodaje.archivo-pequeno", "Archivo pequeño",
        "AV1 por CPU, 1080p, hasta 3 Mbps.", ExportPresetCategory.Quality,
        new ExportSettings
        {
            Video = new VideoExportSettings
            {
                // Calibrado con VMAF (fase 6): CRF 38 con techo de 3 Mbps; en la escena de aceptación da VMAF ≥99 por debajo del techo.
                Codec = ExportCodec.Av1, Width = 1920, Height = 1080,
                RateControl = RateControl.ConstantQuality(38) with { MaxBitrateKbps = 3_000, BufferKbps = 6_000 },
            },
            Audio = new AudioExportSettings { Codec = ExportCodec.Opus, RateControl = new() { Mode = RateControlMode.Vbr, BitrateKbps = 96 } },
        });

    public static ExportPreset ProResMaster { get; } = Factory("rodaje.maestro-prores", "Maestro ProRes 422 HQ",
        "ProRes 422 HQ de 10 bits en MOV con PCM de 24 bits, para seguir editando o entregar.", ExportPresetCategory.Master,
        new ExportSettings
        {
            Container = ExportContainer.Mov,
            Video = new VideoExportSettings
            {
                Codec = ExportCodec.ProRes, Profile = "hq", BitDepth = 10, Chroma = ChromaSubsampling.Chroma422,
                RateControl = RateControl.ProfileDefined,
            },
            Audio = new AudioExportSettings { Codec = ExportCodec.Pcm, RateControl = RateControl.Lossless },
            FastStart = false,
        });

    public static ExportPreset Gif { get; } = Factory("rodaje.gif", "GIF animado",
        "480 px de ancho a 15 fps, sin audio.", ExportPresetCategory.Web,
        new ExportSettings
        {
            Container = ExportContainer.Gif,
            Video = new VideoExportSettings
            {
                Codec = ExportCodec.Gif, Width = 480, FrameRate = new Rational(15, 1), RateControl = RateControl.ProfileDefined,
                Color = ExportColor.Srgb, Chroma = ChromaSubsampling.Chroma444,
            },
        });

    public static ExportPreset AudioOnly { get; } = Factory("rodaje.solo-audio", "Solo audio",
        "AAC de 256 kbps en M4A, normalizado a −14 LUFS.", ExportPresetCategory.AudioOnly,
        new ExportSettings
        {
            Container = ExportContainer.M4a,
            Audio = Aac192 with { RateControl = new() { Mode = RateControlMode.Cbr, BitrateKbps = 256 }, Loudness = LoudnessTarget.Platforms },
        });

    /// <summary>En el orden de las tarjetas del diálogo.</summary>
    public static IReadOnlyList<ExportPreset> All { get; } =
        [YouTube1080p, YouTube4K, Vertical, Square, BestQuality, SmallFile, ProResMaster, Gif, AudioOnly];

    private static ExportPreset Factory(string id, string name, string description, ExportPresetCategory category, ExportSettings settings) =>
        new(id, name, description, category, ExportPresetOrigin.Factory, settings);
}
