using Rodaje.Media.Export;

namespace Rodaje.Media.Tests.Export;

/// <summary>
/// <c>ExportVerifier.Read</c> llamaba a la medida de cuadros clave (que ejecuta ffprobe) dentro del <c>try</c> que traduce
/// un JSON raro a «ffprobe devolvió algo que no se entiende»: un fallo de esa segunda ejecución salía con ese mensaje
/// equivocado y sin su tipo. El <c>try</c> cubre ahora solo la lectura del JSON.
/// </summary>
public sealed class ExportVerifierGopOutsideTryTests : IDisposable
{
    private const string Json = """
        {"format":{"format_name":"mov","duration":"2.0"},
         "streams":[{"index":3,"codec_type":"video","codec_name":"h264","width":1920,"height":1080,"nb_read_packets":"60"}]}
        """;

    private readonly string _file = Path.Combine(Path.GetTempPath(), $"rodaje-verificador-{Guid.NewGuid():N}.bin");

    public ExportVerifierGopOutsideTryTests() => File.WriteAllBytes(_file, new byte[64]);

    public void Dispose() => File.Delete(_file);

    [Fact]
    public void UnFalloDeLaMedidaDeCuadrosClave_SaleConSuPropioError()
    {
        var failure = new InvalidOperationException("ffprobe no terminó");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => ExportVerifier.Read(Json, _file, _ => throw failure));

        Assert.Same(failure, ex);
    }

    [Fact]
    public void LaMedidaDeCuadrosClave_LlegaAlInforme()
    {
        int asked = -1;

        ExportReport report = ExportVerifier.Read(Json, _file, index =>
        {
            asked = index;
            return (48, 3);
        });

        Assert.Equal(3, asked);
        Assert.NotNull(report.Video);
        Assert.Equal((48, 3), (report.Video.MaxGop, report.Video.KeyframeCount));
        Assert.Equal((1920, 1080, 60L), (report.Video.Width, report.Video.Height, report.Video.FrameCount));
    }

    [Fact]
    public void UnJsonRaro_SigueSiendoErrorDeExportacion_SinMedirCuadrosClave()
    {
        bool asked = false;

        Assert.Throws<ExportException>(() => ExportVerifier.Read("""{"format":{},"streams":[{"index":0,"codec_type":"video"}],"chapters":{}}""", _file, _ =>
        {
            asked = true;
            return (0, 0);
        }));

        Assert.False(asked);
    }
}
