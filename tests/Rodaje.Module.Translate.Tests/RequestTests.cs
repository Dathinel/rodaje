using System.Text;
using System.Text.Json;
using Rodaje.Module.Translate.Translation;
using Rodaje.Modules.Protocol;

namespace Rodaje.Module.Translate.Tests;

public sealed class RequestTests
{
    internal static JsonElement Json(string json)
    {
        using JsonDocument d = JsonDocument.Parse(json);
        return d.RootElement.Clone();
    }

    private static void Rejected(string json, string fragment, byte[]? input = null)
    {
        ModuleCallException e = Assert.Throws<ModuleCallException>(() => TranslationRequest.Parse(Json(json), input));
        Assert.Equal(ErrorCodes.InvalidParameters, e.Code);
        Assert.Contains(fragment, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Defaults_are_auto_source_42_characters_and_batches_of_8()
    {
        TranslationRequest r = TranslationRequest.Parse(Json("""{"target":"en","segments":[{"id":"a","text":"Hola"}]}"""));
        Assert.Null(r.Source);
        Assert.Equal("en", r.Target.Code);
        Assert.Equal(42, r.MaxLineChars);
        Assert.Equal(8, r.BatchSize);
        Assert.Equal("auto", r.Device);
        Assert.Equal(-1, r.GpuLayers);
        Assert.Equal([new SourceSegment("a", "Hola")], r.Segments);
    }

    [Fact]
    public void Segments_can_come_in_the_input_buffer_with_trailing_zeros()
    {
        byte[] input = new byte[256];
        Encoding.UTF8.GetBytes("""{"segments":[{"id":"x","text":"Uno"},{"id":"y","text":""}]}""").CopyTo(input, 0);
        TranslationRequest r = TranslationRequest.Parse(Json("""{"source":"es","target":"pt","device":"cpu","batch":4}"""), input);
        Assert.Equal(["x", "y"], r.Segments.Select(s => s.Id));
        Assert.Equal("pt-BR", r.Target.Code);
        Assert.Equal("cpu", r.Device);
        Assert.Equal(4, r.BatchSize);
    }

    [Fact]
    public void Bad_requests_are_rejected_with_a_reason()
    {
        Rejected("""{"segments":[{"id":"a","text":"x"}]}""", "Falta «target»");
        Rejected("""{"target":"klingon","segments":[{"id":"a","text":"x"}]}""", "klingon");
        Rejected("""{"source":"es","target":"es","segments":[{"id":"a","text":"x"}]}""", "el mismo");
        Rejected("""{"target":"en"}""", "Faltan los subtítulos");
        Rejected("""{"target":"en","segments":[]}""", "No hay subtítulos");
        Rejected("""{"target":"en","segments":{"id":"a"}}""", "tiene que ser una lista");
        Rejected("""{"target":"en","segments":[{"id":"a","text":"x"},{"id":"a","text":"y"}]}""", "repetido");
        Rejected("""{"target":"en","segments":[{"id":"","text":"x"}]}""", "«id»");
        Rejected("""{"target":"en","segments":[{"id":"a"}]}""", "«text»");
        Rejected($$"""{"target":"en","segments":[{"id":"a","text":"{{new string('x', 501)}}"}]}""", "500");
        Rejected("""{"target":"en","device":"npu","segments":[{"id":"a","text":"x"}]}""", "device");
        Rejected("""{"target":"en","maxLineChars":5,"segments":[{"id":"a","text":"x"}]}""", "maxLineChars");
        Rejected("""{"target":"en","segments":[{"id":"a","text":"x"}]}""", "no en los dos", Encoding.UTF8.GetBytes("""{"segments":[]}"""));
        Rejected("""{"target":"en"}""", "no es JSON", Encoding.UTF8.GetBytes("{nope"));
    }

    [Fact]
    public void More_than_the_maximum_is_rejected()
    {
        string many = string.Join(',', Enumerable.Range(0, TranslationRequest.MaxSegments + 1).Select(i => $$"""{"id":"c{{i}}","text":"t"}"""));
        Rejected($$"""{"target":"en","segments":[{{many}}]}""", "máximo");
    }
}
