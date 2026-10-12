using Rodaje.Core.Timing;
using Rodaje.Media.Export;

namespace Rodaje.Export.Tests.Cpu;

/// <summary>
/// Ajustes y argumentos de cada exportación por CPU de las pruebas, armados por el código real: <see cref="ExportSettings"/>
/// con los valores por defecto (el primer control de tasa del codificador, el GOP de 2 s, el perfil por formato de píxel),
/// <see cref="ExportSettingsValidator"/> y <see cref="FfmpegArguments.Build"/>. Los codificadores salen del catálogo real
/// (<see cref="ExportEncoderCatalog"/>), con sus <see cref="EncoderSpec.BaseArguments"/>.
/// </summary>
internal static class CpuExportArguments
{
    /// <summary>Preset de SVT-AV1: el del catálogo es 8; aquí 12 para que la prueba sea corta en un runner de dos núcleos.</summary>
    public const string SvtAv1Preset = "12";

    public static EncoderSpec Spec(string id) => ExportEncoderCatalog.Find(id) ?? Rodaje.Export.Encoders.GplEncoderCandidates.All.FirstOrDefault(e => e.Id == id)
        ?? throw new ArgumentException($"{id} no está en el catálogo.", nameof(id));

    /// <summary>Ajustes de una exportación con estos codificadores, como los dejaría el diálogo sin tocar «Avanzado».</summary>
    /// <param name="container">Contenedor de la salida.</param>
    /// <param name="video">Codificador de video, o <c>null</c> para solo audio.</param>
    /// <param name="pixels">Formato de píxel del video (da los bits, el croma y el alfa).</param>
    /// <param name="audio">Codificador de audio, o <c>null</c> para solo video.</param>
    public static ExportSettings Settings(ExportContainer container, string? video, ExportPixelFormat? pixels, string? audio)
    {
        VideoExportSettings? videoSettings = null;
        if (video is not null)
        {
            EncoderSpec spec = Spec(video);
            ExportPixelFormat format = pixels ?? throw new ArgumentNullException(nameof(pixels));
            videoSettings = new VideoExportSettings
            {
                Codec = spec.Codec,
                EncoderId = video,
                RateControl = new RateControl { Mode = spec.RateControls[0] },
                SpeedPreset = video == "libsvtav1" ? SvtAv1Preset : null,
                BitDepth = format.BitDepth(),
                Chroma = format.Chroma(),
                Alpha = format.HasAlpha(),
            };
        }

        AudioExportSettings? audioSettings = null;
        if (audio is not null)
        {
            EncoderSpec spec = Spec(audio);
            RateControlMode mode = spec.RateControls[0];
            audioSettings = new AudioExportSettings
            {
                Codec = spec.Codec,
                EncoderId = audio,
                // 160 kbps vale en todos, también en el AAC de Windows (96, 128, 160 o 192).
                RateControl = new RateControl
                {
                    Mode = mode,
                    BitrateKbps = mode is RateControlMode.Cbr or RateControlMode.Vbr or RateControlMode.Abr ? 160 : null,
                },
            };
        }

        return new ExportSettings { Container = container, Video = videoSettings, Audio = audioSettings };
    }

    /// <summary>
    /// Los argumentos de <see cref="FfmpegArguments.Build"/> para <see cref="Settings"/>, tras comprobar que el validador no
    /// ve problemas: así la prueba usa lo mismo que una exportación de la app.
    /// </summary>
    public static EncoderArguments Build(ExportContainer container, string? video, ExportPixelFormat? pixels, string? audio, int frames)
    {
        ExportSettings settings = Settings(container, video, pixels, audio);
        EncoderSpec? videoSpec = video is null ? null : Spec(video);
        EncoderSpec? audioSpec = audio is null ? null : Spec(audio);
        IReadOnlyList<ExportProblem> problems = ExportSettingsValidator.Validate(settings, videoSpec, audioSpec);
        Assert.True(problems.Count == 0, string.Join(" ", problems.Select(p => p.Message)));
        return FfmpegArguments.Build(settings, videoSpec, pixels, audioSpec, Rational.Fps30, TimeSpan.FromSeconds(frames / 30.0));
    }

    /// <summary>Nombre del códec que da ffprobe.</summary>
    public static string ProbeCodec(string encoder) => encoder switch
    {
        "h264_mf" or "libx264" => "h264",
        "hevc_mf" or "libx265" => "hevc",
        "libsvtav1" => "av1",
        "libvpx-vp9" => "vp9",
        "prores_ks" => "prores",
        "dnxhd" => "dnxhd",
        "ffv1" => "ffv1",
        "aac_mf" or "aac" => "aac",
        "libopus" => "opus",
        _ => encoder,
    };
}
