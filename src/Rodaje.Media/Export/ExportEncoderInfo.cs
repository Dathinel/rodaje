using System.Collections.Immutable;
using System.Globalization;

namespace Rodaje.Media.Export;

/// <summary>Códec de video de salida.</summary>
public enum ExportCodec
{
    H264,
    Hevc,
    Av1,
    Vp9,
}

/// <summary>Quién aporta el codificador de hardware (<see cref="None"/> en los de software).</summary>
public enum ExportVendor
{
    None,
    Nvidia,
    Amd,
    Intel,
    Microsoft,
}

/// <summary>Compromiso entre tamaño del archivo y fidelidad; cada codificador lo traduce a su propio parámetro.</summary>
public enum ExportQuality
{
    Draft,
    Balanced,
    High,
}

/// <summary>Compromiso entre tiempo de exportación y eficiencia de compresión.</summary>
public enum ExportSpeed
{
    Fast,
    Balanced,
    Slow,
}

/// <summary>Un codificador de video de FFmpeg que Rodaje podría usar para exportar.</summary>
/// <param name="Id">Nombre del codificador en FFmpeg (<c>-c:v</c>).</param>
/// <param name="DisplayName">Nombre para la interfaz.</param>
/// <param name="Codec">Códec que produce.</param>
/// <param name="Vendor">Fabricante del hardware, o <see cref="ExportVendor.None"/> si es software.</param>
/// <param name="License">Licencia del codificador, tal como la declara la compilación LGPL (ver <c>docs/legal.md</c>).</param>
/// <param name="PatentEncumbered">Si el códec lleva patentes de consorcio y el codificador es de software (no del fabricante).</param>
/// <param name="LowQuality">Si es un codificador de calidad limitada que solo sirve de último recurso.</param>
/// <param name="PixelFormat">Formato de píxel de entrada que acepta el codificador.</param>
public sealed record ExportEncoderInfo(
    string Id,
    string DisplayName,
    ExportCodec Codec,
    ExportVendor Vendor,
    string License,
    bool PatentEncumbered,
    bool LowQuality,
    string PixelFormat)
{
    /// <summary>Si codifica en la GPU o en el codificador de Windows (Media Foundation suele usar la GPU).</summary>
    public bool IsHardware => Vendor != ExportVendor.None;

    /// <summary>
    /// Argumentos base de calidad y velocidad (a continuación de <c>-c:v Id</c>). No incluyen el formato de píxel
    /// (<see cref="PixelFormat"/>), el GOP, el contenedor ni el tamaño; eso lo decide quien exporte.
    /// </summary>
    public ImmutableArray<string> BaseArguments(ExportQuality quality, ExportSpeed speed) =>
        ExportEncoderArguments.For(this, quality, speed);
}

internal static class ExportEncoderArguments
{
    internal static ImmutableArray<string> For(ExportEncoderInfo encoder, ExportQuality quality, ExportSpeed speed)
    {
        ArgumentNullException.ThrowIfNull(encoder);
        if (!Enum.IsDefined(quality))
        {
            throw new ArgumentOutOfRangeException(nameof(quality));
        }

        if (!Enum.IsDefined(speed))
        {
            throw new ArgumentOutOfRangeException(nameof(speed));
        }

        // Índices 0..2 = Draft/Balanced/High y Fast/Balanced/Slow.
        int q = (int)quality;
        int s = (int)speed;
        string[] args = encoder.Id switch
        {
            // NVENC: calidad constante (cq) con tasa variable; p1 (rápido) a p7 (lento).
            "h264_nvenc" or "hevc_nvenc" or "av1_nvenc" =>
                ["-preset", "p" + I(Pick(s, 2, 5, 7)), "-rc", "vbr", "-cq", I(Pick(q, 30, 24, 19)), "-b:v", "0"],

            // AMF: control cqp con el mismo QP en cuadros I y P.
            "h264_amf" or "hevc_amf" or "av1_amf" =>
                ["-quality", Pick(s, "speed", "balanced", "quality"), "-rc", "cqp",
                    "-qp_i", I(Pick(q, 30, 25, 20)), "-qp_p", I(Pick(q, 30, 25, 20))],

            // Quick Sync: calidad global (ICQ); el preajuste va de veryfast a veryslow.
            "h264_qsv" or "hevc_qsv" or "av1_qsv" =>
                ["-preset", Pick(s, "veryfast", "medium", "veryslow"), "-global_quality", I(Pick(q, 30, 25, 20))],

            // Media Foundation: -quality va de 0 a 100; el codificador decide si usa hardware.
            "h264_mf" or "hevc_mf" =>
                ["-rate_control", "quality", "-quality", I(Pick(q, 55, 70, 85)), "-hw_encoding", "true"],

            // SVT-AV1: crf y preajuste de 0 (lento) a 13 (rápido).
            "libsvtav1" => ["-preset", I(Pick(s, 10, 7, 4)), "-crf", I(Pick(q, 38, 31, 25))],

            // libaom: crf sin tasa objetivo; cpu-used de 0 (lento) a 8 (rápido).
            "libaom-av1" => ["-cpu-used", I(Pick(s, 8, 5, 3)), "-crf", I(Pick(q, 40, 32, 26)), "-b:v", "0", "-row-mt", "1"],

            // libvpx VP9: crf sin tasa objetivo, modo good.
            "libvpx-vp9" => ["-deadline", "good", "-cpu-used", I(Pick(s, 4, 2, 1)), "-crf", I(Pick(q, 38, 32, 27)), "-b:v", "0", "-row-mt", "1"],

            // kvazaar (HEVC): el QP va con -crf y el preajuste por kvazaar-params.
            "libkvazaar" => ["-crf", I(Pick(q, 33, 28, 23)), "-kvazaar-params",
                "preset=" + Pick(s, "ultrafast", "fast", "slow")],

            // OpenH264: solo control por calidad; no tiene preajustes de velocidad que valga la pena usar.
            "libopenh264" => ["-rc_mode", "quality", "-allow_skip_frames", "0"],

            _ => throw new ArgumentException($"Codificador desconocido: {encoder.Id}.", nameof(encoder)),
        };
        return [.. args];
    }

    private static T Pick<T>(int index, T first, T second, T third) => index switch { 0 => first, 1 => second, _ => third };

    private static string I(int value) => value.ToString(CultureInfo.InvariantCulture);
}
