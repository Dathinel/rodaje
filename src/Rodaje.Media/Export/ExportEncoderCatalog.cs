using System.Collections.Immutable;

namespace Rodaje.Media.Export;

/// <summary>
/// Catálogo de codificadores de salida en orden de preferencia: primero el hardware (por fabricante), luego el
/// software LGPL. Solo hay lo que lleva la compilación LGPL de FFmpeg (<c>docs/ffmpeg-configure.txt</c>); x264 y
/// x265 (GPL) no existen aquí ni deben añadirse.
/// </summary>
public static class ExportEncoderCatalog
{
    private const string VendorLicense = "Controlador del fabricante (no se distribuye con Rodaje)";

    /// <summary>Todos los codificadores conocidos, del más al menos preferido dentro de su grupo.</summary>
    public static ImmutableArray<ExportEncoderInfo> All { get; } =
    [
        // Hardware.
        Hw("h264_nvenc", "NVIDIA NVENC (H.264)", ExportCodec.H264, ExportVendor.Nvidia, "yuv420p"),
        Hw("hevc_nvenc", "NVIDIA NVENC (HEVC)", ExportCodec.Hevc, ExportVendor.Nvidia, "yuv420p"),
        Hw("av1_nvenc", "NVIDIA NVENC (AV1)", ExportCodec.Av1, ExportVendor.Nvidia, "yuv420p"),
        Hw("h264_amf", "AMD AMF (H.264)", ExportCodec.H264, ExportVendor.Amd, "nv12"),
        Hw("hevc_amf", "AMD AMF (HEVC)", ExportCodec.Hevc, ExportVendor.Amd, "nv12"),
        Hw("av1_amf", "AMD AMF (AV1)", ExportCodec.Av1, ExportVendor.Amd, "nv12"),
        Hw("h264_qsv", "Intel Quick Sync (H.264)", ExportCodec.H264, ExportVendor.Intel, "nv12"),
        Hw("hevc_qsv", "Intel Quick Sync (HEVC)", ExportCodec.Hevc, ExportVendor.Intel, "nv12"),
        Hw("av1_qsv", "Intel Quick Sync (AV1)", ExportCodec.Av1, ExportVendor.Intel, "nv12"),
        Hw("h264_mf", "Media Foundation (H.264)", ExportCodec.H264, ExportVendor.Microsoft, "nv12"),
        Hw("hevc_mf", "Media Foundation (HEVC)", ExportCodec.Hevc, ExportVendor.Microsoft, "nv12"),

        // Software, todo compatible con la LGPL. AV1 y VP9 son formatos libres de regalías; HEVC y H.264 por CPU
        // llevan patentes (docs/legal.md), así que van al final y no son la elección por defecto si hay otra.
        new("libsvtav1", "SVT-AV1 (AV1)", ExportCodec.Av1, ExportVendor.None, "BSD-3-Clause-Clear", false, false, "yuv420p"),
        new("libaom-av1", "libaom (AV1)", ExportCodec.Av1, ExportVendor.None, "BSD-2-Clause", false, false, "yuv420p"),
        new("libvpx-vp9", "libvpx (VP9)", ExportCodec.Vp9, ExportVendor.None, "BSD-3-Clause", false, false, "yuv420p"),
        new("libkvazaar", "kvazaar (HEVC)", ExportCodec.Hevc, ExportVendor.None, "BSD-3-Clause", true, false, "yuv420p"),
        new("libopenh264", "OpenH264 (H.264, calidad baja)", ExportCodec.H264, ExportVendor.None, "BSD-2-Clause", true, true, "yuv420p"),
    ];

    public static ExportEncoderInfo? Find(string id) =>
        All.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.Ordinal));

    private static ExportEncoderInfo Hw(string id, string name, ExportCodec codec, ExportVendor vendor, string pixelFormat) =>
        new(id, name, codec, vendor, VendorLicense, false, false, pixelFormat);
}
