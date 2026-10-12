using Rodaje.Media.Export;

namespace Rodaje.App.Views.Export;

/// <summary>
/// El catálogo (<see cref="ExportEncoderCatalog"/>) sin sonda, con lo que daría una RTX 3050 en Windows: sin AMD, sin Intel
/// y sin el AV1 de NVENC (solo RTX 40). Para la simulación y las pruebas.
/// </summary>
public static class SimulatedEncoders
{
    public static IReadOnlyList<EncoderSpec> All { get; } =
        [.. ExportEncoderCatalog.All.Where(e => e.Vendor is not (EncoderVendor.Amd or EncoderVendor.Intel) && e.Id != "av1_nvenc")];

    public static EncoderSpec AacMf => Find("aac_mf");

    public static EncoderSpec H264Nvenc => Find("h264_nvenc");

    public static EncoderSpec HevcNvenc => Find("hevc_nvenc");

    public static EncoderSpec Mp3 => Find("libmp3lame");

    public static EncoderSpec ProRes => Find("prores_ks");

    public static EncoderSpec SvtAv1 => Find("libsvtav1");

    private static EncoderSpec Find(string id) => ExportEncoderCatalog.Find(id)!;
}
