using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Media.Export;

namespace Rodaje.Media.Tests.Export;

/// <summary>Color HDR de la salida: etiquetas BT.2020 con PQ o HLG y metadatos HDR10 en la entrada del pipe.</summary>
public sealed class FfmpegPipeHdrArgumentsTests
{
    [Fact]
    public void Pq_puts_mastering_display_and_content_light_before_the_pipe_and_tags_both_sides()
    {
        var video = new ExportVideoFormat(320, 180, Rational.Fps30, ExportPixelFormat.P010, ExportColor.Hdr10 with { MaxCll = 1000, MaxFall = 400 });
        var request = new ExportSinkRequest(@"C:\v\salida.mp4", ExportContainer.Mp4, video, SinkEncoders.For("hevc_nvenc"), [], null, null, null, [], 30);

        List<string> args = [.. FfmpegPipeArguments.Build(request, @"C:\salida.mp4.rodaje-tmp", null)];

        int pipe = args.IndexOf("pipe:0");
        Assert.True(args.IndexOf("-mastering_display") is var md && md > 0 && md < pipe, string.Join(' ', args));
        Assert.Equal(ExportColor.DefaultMasteringDisplay, args[md + 1]);
        Assert.True(args.IndexOf("-content_light") is var cl && cl > 0 && cl < pipe);
        Assert.Equal("1000,400", args[cl + 1]);
        Assert.Equal(2, args.Count(a => a == "smpte2084"));
        Assert.Equal(2, args.Count(a => a == "bt2020nc"));
        Assert.Equal(2, args.Count(a => a == "bt2020"));
    }

    [Fact]
    public void Hlg_tags_the_curve_without_mastering_metadata_and_sdr_has_neither()
    {
        Assert.Equal(["-colorspace", "bt2020nc", "-color_primaries", "bt2020", "-color_trc", "arib-std-b67", "-color_range", "tv", "-chroma_sample_location", "left"],
            ExportColor.Hlg.FfmpegArguments());
        Assert.Empty(ExportColor.Hlg.FfmpegHdrInputArguments());
        Assert.Empty(ExportColor.Bt709Limited.FfmpegHdrInputArguments());
        Assert.Equal(["-mastering_display", ExportColor.DefaultMasteringDisplay], ExportColor.Hdr10.FfmpegHdrInputArguments());
    }

    [Theory]
    [InlineData(SequenceColorSpace.HdrPq, ExportPixelFormat.P010, ExportTransfer.Pq, ExportMatrix.Bt2020)]
    [InlineData(SequenceColorSpace.HdrHlg, ExportPixelFormat.Yuv422P10, ExportTransfer.Hlg, ExportMatrix.Bt2020)]
    [InlineData(SequenceColorSpace.HdrPq, ExportPixelFormat.Nv12, ExportTransfer.Bt709, ExportMatrix.Bt709)]
    [InlineData(SequenceColorSpace.HdrPq, ExportPixelFormat.Rgba, ExportTransfer.Srgb, ExportMatrix.Bt709)]
    [InlineData(SequenceColorSpace.Sdr709, ExportPixelFormat.P010, ExportTransfer.Bt709, ExportMatrix.Bt709)]
    public void The_default_color_follows_the_sequence_only_with_ten_bit_yuv(SequenceColorSpace space, ExportPixelFormat pixels,
        ExportTransfer transfer, ExportMatrix matrix)
    {
        ExportColor color = ExportColor.For(1920, 1080, pixels, space);

        Assert.Equal(transfer, color.Transfer);
        Assert.Equal(matrix, color.Matrix);
        Assert.Equal(color.Transfer is ExportTransfer.Pq or ExportTransfer.Hlg, color.IsHdr);
    }

    [Fact]
    public void Sdr_colors_are_exactly_what_they_were()
    {
        Assert.Equal(new ExportColor(ExportMatrix.Bt709), ExportColor.For(1920, 1080, ExportPixelFormat.P010));
        Assert.Equal(new ExportColor(ExportMatrix.Bt601), ExportColor.For(720, 576));
        Assert.Equal(["-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709", "-color_range", "tv", "-chroma_sample_location", "left"],
            ExportColor.Bt709Limited.FfmpegArguments());
    }
}
