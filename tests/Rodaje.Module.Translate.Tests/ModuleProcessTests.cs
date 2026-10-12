using System.Text;
using System.Text.Json;
using Rodaje.Module.Translate.Translation;
using Rodaje.Modules.Protocol;
using Xunit.Abstractions;

namespace Rodaje.Module.Translate.Tests;

/// <summary>
/// El módulo como proceso aparte, por el protocolo, como lo usa la app. La tubería y la memoria compartida son de
/// Windows: en Linux estas pruebas no corren (categoría SoloWindows).
/// </summary>
[Collection("Qwen")]
[Trait("Categoria", "SoloWindows")]
public sealed class ModuleProcessTests(ITestOutputHelper output)
{
    private static string Exe =>
        Environment.GetEnvironmentVariable("RODAJE_MODULO_TRADUCIR") is { Length: > 0 } exe ? exe : Path.Combine(AppContext.BaseDirectory, "Rodaje.Module.Translate.exe");

    [Fact]
    public async Task Bad_requests_and_a_missing_model_are_explained_without_closing_the_module()
    {
        await using ModuleProcess module = await ModuleProcess.StartAsync(Exe);
        Assert.Equal("modulo-traducir", module.Client.Module.Id);
        Assert.Equal(["traducir"], module.Client.Module.Functions.Select(f => f.Id));

        ModuleException error = await Assert.ThrowsAnyAsync<ModuleException>(() =>
            module.Client.InvokeAsync("traducir", RequestTests.Json("""{"target":"xx","segments":[{"id":"a","text":"Hola"}]}""")));
        Assert.Equal(ErrorCodes.InvalidParameters, error.Code);

        string missing = Path.Combine(Path.GetTempPath(), "no-existe-" + Guid.NewGuid().ToString("N") + ".gguf");
        string json = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["target"] = "en",
            ["model"] = missing,
            ["segments"] = new[] { new Dictionary<string, string> { ["id"] = "a", ["text"] = "Hola" } },
        });
        error = await Assert.ThrowsAnyAsync<ModuleException>(() => module.Client.InvokeAsync("traducir", RequestTests.Json(json)));
        Assert.Equal(ErrorCodes.InvalidParameters, error.Code);
        Assert.Contains("Falta el modelo", error.Message, StringComparison.Ordinal);

        SharedMemoryRegion region = module.Client.CreateRegion(4096);
        error = await Assert.ThrowsAnyAsync<ModuleException>(() => module.Client.InvokeAsync("traducir", RequestTests.Json("""{"target":"en"}"""),
            [region.Describe(0, 4096, audio: new AudioLayout(16000, 1, 1024))]));
        Assert.Equal(ErrorCodes.InvalidParameters, error.Code);

        await module.Client.PingAsync();
        Assert.False(module.Exited.IsCompleted);
    }

    [ModelFact]
    public async Task Translates_through_the_protocol_with_input_and_output_buffers()
    {
        await using ModuleProcess module = await ModuleProcess.StartAsync(Exe);
        byte[] input = Encoding.UTF8.GetBytes("""{"segments":[{"id":"c1","text":"Hola a todos."},{"id":"c2","text":""},{"id":"c3","text":"Nos vemos mañana."}]}""");
        SharedMemoryRegion inRegion = module.Client.CreateRegion(input.Length);
        input.CopyTo(inRegion.GetSpan(0, input.Length));
        SharedMemoryRegion outRegion = module.Client.CreateRegion(1 << 16);
        List<ModuleProgress> progress = [];
        ModuleResult result = await module.Client.InvokeAsync("traducir",
            RequestTests.Json($$"""{"source":"es","target":"en","model":{{JsonSerializer.Serialize(ModelFactAttribute.ModelPath)}}}"""),
            [inRegion.Describe(0, input.Length), outRegion.Describe(0, 1 << 16)],
            new Progress<ModuleProgress>(p => { lock (progress) { progress.Add(p); } }));
        int bytes = result.Value!.Value.GetProperty("bytes").GetInt32();
        TranslationResult r = JsonSerializer.Deserialize(outRegion.GetSpan(0, bytes), TranslationJson.Default.TranslationResult)!;
        output.WriteLine($"{r.Device}: " + string.Join(" | ", r.Segments.Select(s => s.Text)));
        Assert.Equal(["c1", "c2", "c3"], r.Segments.Select(s => s.Id));
        Assert.Equal("", r.Segments[1].Text);
        Assert.Contains("everyone", r.Segments[0].Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("tomorrow", r.Segments[2].Text, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(progress);
    }
}
