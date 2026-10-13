using System.Text.Json;
using Rodaje.App.Views.Subtitles;
using Rodaje.Modules.Protocol;

namespace Rodaje.App.Tests.Subtitles;

/// <summary>
/// <c>bytes</c> con otro tipo en el resultado de <c>subtitulos</c>: <c>TryGetInt64</c> lanzaba
/// <see cref="InvalidOperationException"/> en vez de <see cref="ModuleProtocolException"/>.
/// </summary>
public sealed class AutoSubtitlesResultTypesTests
{
    [Theory]
    [InlineData("""{"bytes":"10"}""")]
    [InlineData("""{"bytes":null}""")]
    [InlineData("""{"bytes":{}}""")]
    [InlineData("""{"bytes":[10]}""")]
    [InlineData("""{"bytes":false}""")]
    public void UnTamanoQueNoEsNumero_EsUnErrorDeProtocolo(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.Throws<ModuleProtocolException>(() => AutoSubtitles.FromResult(doc.RootElement, """{"segments":[]}"""u8));
    }
}
