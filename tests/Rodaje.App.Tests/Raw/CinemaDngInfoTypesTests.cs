using System.Text.Json;
using Rodaje.App.Views.Raw;
using Rodaje.Modules.Protocol;

namespace Rodaje.App.Tests.Raw;

/// <summary>
/// La respuesta de <c>info</c> con tipos cambiados: <c>TryGetInt32</c> lanzaba <see cref="InvalidOperationException"/> en vez
/// de <see cref="ModuleProtocolException"/> (tamaño) o de caer a 24 cuadros por segundo (cadencia).
/// </summary>
public sealed class CinemaDngInfoTypesTests
{
    [Theory]
    [InlineData("""{"ancho":"640","alto":360}""")]
    [InlineData("""{"ancho":640,"alto":null}""")]
    [InlineData("""{"ancho":{},"alto":360}""")]
    [InlineData("""{"ancho":640,"alto":[360]}""")]
    public void UnTamanoQueNoEsNumero_EsUnErrorDeProtocolo(string json) =>
        Assert.Throws<ModuleProtocolException>(() => CinemaDngImport.ReadInfo(Json(json)));

    [Theory]
    [InlineData("""{"num":"25","den":1}""")]
    [InlineData("""{"num":25,"den":null}""")]
    [InlineData("""{"num":true,"den":{}}""")]
    public void UnaCadenciaQueNoEsNumero_QuedaEn24(string rate)
    {
        (int width, int height, int num, int den, string camera) = CinemaDngImport.ReadInfo(Json($$"""{"ancho":640,"alto":360,"cadencia":{{rate}},"marca":"Cam","modelo":"X"}"""));
        Assert.Equal((640, 360, 24, 1, "Cam X"), (width, height, num, den, camera));
    }

    [Fact]
    public void UnaRespuestaBuena_SeLeeEntera()
    {
        (int width, int height, int num, int den, _) = CinemaDngImport.ReadInfo(Json("""{"ancho":640,"alto":360,"cadencia":{"num":25,"den":1}}"""));
        Assert.Equal((640, 360, 25, 1), (width, height, num, den));
    }

    private static JsonElement Json(string text)
    {
        using JsonDocument doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }
}
