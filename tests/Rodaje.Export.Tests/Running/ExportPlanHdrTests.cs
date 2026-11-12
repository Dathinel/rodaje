using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Export.Running;
using Rodaje.Media.Export;

namespace Rodaje.Export.Tests.Running;

/// <summary>El color de la salida sigue al espacio de la secuencia solo en HEVC, AV1 y ProRes de 10 bits. Sin FFmpeg ni GPU.</summary>
public sealed class ExportPlanHdrTests
{
    private static readonly IReadOnlyList<EncoderSpec> Encoders = ExportEncoderCatalog.All;

    [Theory]
    [InlineData(SequenceColorSpace.HdrPq, ExportCodec.Hevc, 10, ExportTransfer.Pq)]
    [InlineData(SequenceColorSpace.HdrHlg, ExportCodec.Hevc, 10, ExportTransfer.Hlg)]
    [InlineData(SequenceColorSpace.HdrPq, ExportCodec.Av1, 10, ExportTransfer.Pq)]
    [InlineData(SequenceColorSpace.HdrPq, ExportCodec.ProRes, 10, ExportTransfer.Pq)]
    [InlineData(SequenceColorSpace.HdrPq, ExportCodec.Hevc, 8, ExportTransfer.Bt709)]
    [InlineData(SequenceColorSpace.HdrPq, ExportCodec.Vp9, 10, ExportTransfer.Bt709)]
    [InlineData(SequenceColorSpace.Sdr709, ExportCodec.Hevc, 10, ExportTransfer.Bt709)]
    public void The_plan_picks_the_color_from_the_sequence(SequenceColorSpace space, ExportCodec codec, int bits, ExportTransfer expected)
    {
        Project project = ExportPlanTests.Project(Time.FromSeconds(2));
        project.Sequences[0].ColorSpace = space;
        var settings = new ExportSettings
        {
            Container = codec switch { ExportCodec.ProRes => ExportContainer.Mov, ExportCodec.Vp9 => ExportContainer.Mkv, _ => ExportContainer.Mp4 },
            Video = new VideoExportSettings
            {
                Codec = codec, BitDepth = bits,
                Chroma = codec == ExportCodec.ProRes ? ChromaSubsampling.Chroma422 : ChromaSubsampling.Chroma420,
                RateControl = codec == ExportCodec.ProRes ? RateControl.ProfileDefined : RateControl.ConstantQuality(),
            },
        };

        ExportPlan plan = ExportPlan.Create(ExportPlanTests.Request(project, settings), Encoders);

        Assert.Equal(expected, plan.Video!.Color.Transfer);
        Assert.Equal(expected is ExportTransfer.Pq or ExportTransfer.Hlg ? ExportMatrix.Bt2020 : ExportMatrix.Bt709, plan.Video.Color.Matrix);
    }

    [Fact]
    public void An_explicit_color_wins_over_the_sequence()
    {
        Project project = ExportPlanTests.Project(Time.FromSeconds(2));
        project.Sequences[0].ColorSpace = SequenceColorSpace.HdrPq;
        var settings = new ExportSettings
        {
            Container = ExportContainer.Mp4,
            Video = new VideoExportSettings { Codec = ExportCodec.Hevc, BitDepth = 10, Color = ExportColor.Bt709Limited },
        };

        Assert.Equal(ExportColor.Bt709Limited, ExportPlan.Create(ExportPlanTests.Request(project, settings), Encoders).Video!.Color);
    }
}
