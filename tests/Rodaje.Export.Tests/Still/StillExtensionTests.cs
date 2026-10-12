using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Export.Still;
using Rodaje.Media.Export;

namespace Rodaje.Export.Tests.Still;

/// <summary>La extensión del fotograma dice siempre lo que es: sin GPU ni procesos, corre también en Linux.</summary>
public sealed class StillExtensionTests
{
    private static Project Project() => new()
    {
        Id = "p",
        Name = "Fotograma",
        Sequences = [new Sequence { Id = "sec", Name = "Sec", Width = 1920, Height = 1080, FrameRate = Rational.Fps30 }],
    };

    private static StillPlan Plan(string path, StillFormat format) =>
        StillPlan.Create(new StillRequest(Project(), "sec", Time.Zero, path, new StillSettings { Format = format }));

    [Theory]
    [InlineData("foto.png", StillFormat.Png)]
    [InlineData("FOTO.PNG", StillFormat.Png)]
    [InlineData("foto.jpg", StillFormat.Jpeg)]
    [InlineData("foto.JPEG", StillFormat.Jpeg)]
    public void Las_extensiones_de_imagen_valen(string path, StillFormat format)
    {
        Assert.Null(StillSettings.ExtensionProblem(path, format));
        Assert.Equal(path, StillSettings.WithExtension(path, format));
        Assert.Equal(format, Plan(path, format).Settings.Format);
    }

    [Theory]
    [InlineData("foto.bmp", StillFormat.Png, "«.bmp»")]
    [InlineData("foto.tiff", StillFormat.Png, "«.tiff»")]
    [InlineData("foto.png", StillFormat.Jpeg, "«.png»")]
    [InlineData("foto.jpg", StillFormat.Png, "«.jpg»")]
    [InlineData("foto", StillFormat.Png, "no tiene extensión")]
    public void Otra_extension_se_rechaza_con_ajustes_no_validos(string path, StillFormat format, string reason)
    {
        ExportException error = Assert.Throws<ExportException>(() => Plan(path, format));
        Assert.Equal(ExportErrorCode.InvalidSettings, error.Code);
        Assert.Contains(reason, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("foto.bmp", "foto.bmp.png")]
    [InlineData("foto", "foto.png")]
    [InlineData("Proyecto v1.2 - cuadro 10", "Proyecto v1.2 - cuadro 10.png")]
    [InlineData("foto.png", "foto.png")]
    public void La_ruta_del_dialogo_recibe_la_extension_del_formato(string chosen, string expected)
    {
        // Lo que hace «Exportar fotograma»: el formato sale de la extensión y, si no es de imagen, se añade la de PNG.
        string path = StillSettings.WithExtension(chosen, StillSettings.FormatFor(chosen));
        Assert.Equal(expected, path);
        Assert.Null(StillSettings.ExtensionProblem(path, StillSettings.FormatFor(path)));
    }

    [Fact]
    public void Un_jpeg_pedido_en_una_ruta_png_recibe_jpg()
    {
        Assert.Equal("foto.png.jpg", StillSettings.WithExtension("foto.png", StillFormat.Jpeg));
    }
}
