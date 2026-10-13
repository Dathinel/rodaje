using Rodaje.Media.Export;

namespace Rodaje.Media.Tests.Export;

/// <summary>
/// La salida de ffprobe con tipos inesperados: <c>attached_pic</c> o <c>index</c> que no eran número lanzaban
/// InvalidOperationException desde GetInt32, y un JSON sin <c>format</c> salía como KeyNotFoundException en vez de un
/// error de exportación.
/// </summary>
public sealed class ExportVerifierProbeJsonTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"rodaje-verificador-{Guid.NewGuid():N}.bin");

    public ExportVerifierProbeJsonTests() => File.WriteAllBytes(_file, new byte[64]);

    public void Dispose() => File.Delete(_file);

    private static (int, int) NoGop(int index) => (0, 0);

    [Theory]
    [InlineData("\"1\"")]
    [InlineData("null")]
    [InlineData("1.5")]
    [InlineData("{}")]
    public void UnaPortadaConOtroTipo_NoLanzaYElVideoSeLee(string attachedPic)
    {
        string json = $$$"""
            {"format":{"format_name":"mov","duration":"2.0","tags":{"encoder":"x","raro":5}},
             "streams":[{"index":0,"codec_type":"video","codec_name":"h264","width":"ancho","height":1080.5,"nb_read_packets":"60",
                         "disposition":{"attached_pic":{{{attachedPic}}}}}]}
            """;

        ExportReport report = ExportVerifier.Read(json, _file, NoGop);

        Assert.NotNull(report.Video);
        Assert.Equal("h264", report.Video.Codec);
        Assert.Equal(0, report.Video.Width);
        Assert.Equal(0, report.Video.Height);
        Assert.Equal(60, report.Video.FrameCount);
        Assert.Equal("5", report.Tags["raro"]);
    }

    [Theory]
    [InlineData("\"0\"")]
    [InlineData("null")]
    [InlineData("-1")]
    public void UnIndiceQueNoEsNumero_EsErrorDeExportacion(string index)
    {
        string json = $$$"""{"format":{},"streams":[{"index":{{{index}}},"codec_type":"video"}]}""";

        ExportException ex = Assert.Throws<ExportException>(() => ExportVerifier.Read(json, _file, NoGop));

        Assert.Equal(ExportErrorCode.EncoderFailed, ex.Code);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"format\":[],\"streams\":[]}")]
    [InlineData("{\"format\":{\"tags\":[]},\"streams\":[]}")]
    [InlineData("{\"format\":{},\"streams\":{}}")]
    [InlineData("no es json")]
    public void UnJsonQueNoEsDeFfprobe_EsErrorDeExportacion(string json)
    {
        ExportException ex = Assert.Throws<ExportException>(() => ExportVerifier.Read(json, _file, NoGop));

        Assert.Equal(ExportErrorCode.EncoderFailed, ex.Code);
    }
}
