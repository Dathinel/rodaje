using Rodaje.Export.Encoders;
using Rodaje.Media.Export;

namespace Rodaje.App.Views.Export;

/// <summary>El catálogo de candidatos (<see cref="ExportEncoderCandidates"/>) sin sonda: lo que daría una RTX 3050, para la simulación y las pruebas.</summary>
public static class SimulatedEncoders
{
    public static IReadOnlyList<EncoderSpec> All => ExportEncoderCandidates.All;

    public static EncoderSpec AacMf => ExportEncoderCandidates.AacMf;

    public static EncoderSpec H264Nvenc => ExportEncoderCandidates.H264Nvenc;

    public static EncoderSpec HevcNvenc => ExportEncoderCandidates.HevcNvenc;

    public static EncoderSpec Mp3 => ExportEncoderCandidates.Mp3;

    public static EncoderSpec ProRes => ExportEncoderCandidates.ProRes;

    public static EncoderSpec SvtAv1 => ExportEncoderCandidates.SvtAv1;
}
