using Rodaje.Media.Export;
using Rodaje.Media.Gpu;

namespace Rodaje.Export.Tuning;

/// <summary>
/// Ajustes de velocidad por fabricante: el preajuste de cada familia de codificadores en «alto rendimiento» y en «bajo»
/// cuando el usuario deja el de por defecto, y si una exportación ocupa el codificador de la GPU (para la cola).
/// </summary>
/// <remarks>
/// <para>NVENC: p4 en los dos modos. En la RTX 3050, 1080p a calidad constante va un 32 % más rápido con p4 que con p5
/// (240 frente a 181 fps solo codificando); p1–p3 no ganan más y p7 es un 80 % más lento. NVENC es un bloque aparte del
/// 3D, así que en bajo rendimiento un preajuste más lento no deja más GPU a la vista previa.</para>
/// <para>AMF: «speed» en alto rendimiento (VCN es otro bloque que el 3D, la vista previa no lo nota) y «balanced» en bajo.
/// Quick Sync: «faster» en alto rendimiento y «medium» en bajo. Los dos sin probar en un equipo real.</para>
/// <para>SVT-AV1: 9 en alto rendimiento y 8 en bajo (de 10 en adelante SVT-AV1 avisa de artefactos visibles).</para>
/// </remarks>
public static class ExportHardwareTuning
{
    /// <summary>El preajuste de velocidad para el modo, o el de fábrica del codificador si no hay uno afinado.</summary>
    public static string? SpeedPreset(EncoderSpec encoder, ExportPerformance performance)
    {
        ArgumentNullException.ThrowIfNull(encoder);
        bool fastest = performance == ExportPerformance.Fastest;
        string? tuned = encoder.Id switch
        {
            var id when id.EndsWith("_nvenc", StringComparison.Ordinal) => "p4",
            var id when id.EndsWith("_amf", StringComparison.Ordinal) => fastest ? "speed" : "balanced",
            var id when id.EndsWith("_qsv", StringComparison.Ordinal) => fastest ? "faster" : "medium",
            "libsvtav1" => fastest ? "9" : "8",
            _ => null,
        };
        return tuned is not null && encoder.SpeedPresets.Contains(tuned) ? tuned : encoder.DefaultSpeedPreset;
    }

    /// <summary>
    /// Si la exportación codificará en la GPU (NVENC, AMF, Quick Sync) en este equipo. Con el codificador elegido a mano lo
    /// dice su nombre; en automático, si la GPU principal codifica ese códec.
    /// </summary>
    public static bool UsesGpuEncoder(ExportSettings settings, GpuProfile profile)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(profile);
        if (settings.Video is not { } video)
        {
            return false;
        }

        if (video.EncoderId is { } id)
        {
            return id.EndsWith("_nvenc", StringComparison.Ordinal) || id.EndsWith("_amf", StringComparison.Ordinal)
                || id.EndsWith("_qsv", StringComparison.Ordinal);
        }

        HardwareEncodeCodecs needed = video.Codec switch
        {
            ExportCodec.H264 => HardwareEncodeCodecs.H264,
            ExportCodec.Hevc => HardwareEncodeCodecs.Hevc,
            ExportCodec.Av1 => HardwareEncodeCodecs.Av1,
            _ => HardwareEncodeCodecs.None,
        };
        return needed != HardwareEncodeCodecs.None && profile.Primary is { } gpu
            && (!gpu.Generation.Known || gpu.Generation.Encodes(needed));
    }
}
