using Rodaje.Media.Export;

namespace Rodaje.Media.Tests.Export;

/// <summary>
/// Argumentos de cada codificador por CPU con los valores por defecto del catálogo, tal como los arma
/// <c>Rodaje.Export.FfmpegArguments</c> (calidad constante, preset, GOP en cuadros, perfil por formato de píxel, alfa).
/// </summary>
/// <remarks>
/// <c>Rodaje.Media.Tests</c> no referencia <c>Rodaje.Export</c> (que depende de <c>Rodaje.Media</c>), así que las líneas se
/// copian aquí de <c>src/Rodaje.Export/FfmpegArguments.cs</c> y hay que mantenerlas al día si cambia.
/// Los codificadores salen del catálogo real (<see cref="ExportEncoderCatalog"/>), con sus <see cref="EncoderSpec.BaseArguments"/>.
/// </remarks>
internal static class CpuExportArguments
{
    public const int Gop = 30;

    public static EncoderSpec Spec(string id) => ExportEncoderCatalog.Find(id)
        ?? throw new ArgumentException($"{id} no está en el catálogo.", nameof(id));

    /// <summary>Perfil que <c>FfmpegArguments</c> elige por formato de píxel (el primero del catálogo que lo admite).</summary>
    public static string? Profile(string encoder, ExportPixelFormat pixels) =>
        Spec(encoder).Profiles.FirstOrDefault(p => p.Allows(pixels))?.Id;

    public static IReadOnlyList<string> Video(string encoder, ExportPixelFormat pixels)
    {
        EncoderSpec spec = Spec(encoder);
        var args = new List<string>();
        switch (encoder)
        {
            case "h264_mf":
                args.AddRange(["-rate_control", "quality", "-quality", "60"]);
                break;
            case "libsvtav1":
                // El preset por defecto del catálogo es 8; aquí 12 para que la prueba sea corta en un runner de dos núcleos.
                args.AddRange(["-crf", "35", "-preset", "12"]);
                break;
            case "libvpx-vp9":
                args.AddRange(["-crf", "31", "-b:v", "0"]);
                break;
        }

        if (Profile(encoder, pixels) is { } profile)
        {
            args.AddRange(["-profile:v", profile]);
        }

        if (!spec.IntraOnly)
        {
            args.AddRange(["-g", Gop.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        }

        if (pixels.HasAlpha() && encoder == "prores_ks")
        {
            args.AddRange(["-alpha_bits", "16"]);
        }

        return args;
    }

    public static IReadOnlyList<string> Audio(string encoder)
    {
        IReadOnlyList<string> rate = encoder switch
        {
            "aac_mf" => ["-b:a", "192k"],
            "aac" => ["-b:a", "160k", "-profile:a", "aac_low"],
            "libopus" => ["-b:a", "128k", "-vbr", "on"],
            "flac" => ["-sample_fmt", "s32", "-bits_per_raw_sample", "24"],
            _ => [],
        };
        return [.. rate, "-ar", "48000", "-ac", "2"];
    }

    public static IReadOnlyList<string> Container(ExportContainer container) =>
        container is ExportContainer.Mp4 or ExportContainer.Mov or ExportContainer.M4a ? ["-movflags", "+faststart"] : [];

    /// <summary>Nombre del códec que da ffprobe.</summary>
    public static string ProbeCodec(string encoder) => encoder switch
    {
        "h264_mf" => "h264",
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
