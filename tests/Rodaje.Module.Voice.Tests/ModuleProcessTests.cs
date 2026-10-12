using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Rodaje.Modules.Protocol;
using Xunit.Abstractions;

namespace Rodaje.Module.Voice.Tests;

/// <summary>El módulo como proceso aparte, por el protocolo, como lo usa la app.</summary>
[Collection("Kokoro")]
public sealed class ModuleProcessTests(ITestOutputHelper output)
{
    private static string Exe =>
        Environment.GetEnvironmentVariable("RODAJE_MODULO_VOZ") is { Length: > 0 } exe ? exe : Path.Combine(AppContext.BaseDirectory, "Rodaje.Module.Voice.exe");

    private static JsonElement Json(string s)
    {
        using JsonDocument d = JsonDocument.Parse(s);
        return d.RootElement.Clone();
    }

    [ModelFact]
    public async Task Lists_voices_speaks_into_the_buffer_and_delivers_when_it_does_not_fit()
    {
        await using ModuleProcess module = await ModuleProcess.StartAsync(Exe);
        Assert.Equal("modulo-voz", module.Client.Module.Id);

        ModuleResult voices = await module.Client.InvokeAsync("voces");
        JsonElement list = voices.Value!.Value.GetProperty("voices");
        string[] ids = [.. list.EnumerateArray().Select(v => v.GetProperty("id").GetString()!)];
        Assert.Contains("ef_dora", ids);
        Assert.Contains("pm_alex", ids);
        Assert.Contains("af_heart", ids);
        Assert.Equal(3, list.EnumerateArray().Count(v => v.GetProperty("languages").EnumerateArray().Any(l => l.GetString() == "pt-BR")));
        Assert.Equal(5, voices.Value.Value.GetProperty("languages").GetArrayLength());

        SharedMemoryRegion region = module.Client.CreateRegion(60L * 24_000 * 4);
        SharedBufferRef buffer = region.Describe(0, region.Capacity, audio: new AudioLayout(24_000, 1, 60L * 24_000));
        List<ModuleProgress> progress = [];
        ModuleResult r = await module.Client.InvokeAsync("voz", Json("""{"text":"Hola, esto es una prueba.","language":"es-419","voice":"em_alex"}"""),
            [buffer], new Progress<ModuleProgress>(progress.Add));
        Assert.True(r.Value!.Value.GetProperty("written").GetBoolean());
        long frames = r.Value.Value.GetProperty("frames").GetInt64();
        Assert.InRange(frames, 24_000 / 2, 24_000 * 5);
        Assert.Equal(frames * 4, r.Buffers.Single().Length);
        float[] audio = MemoryMarshal.Cast<byte, float>(region.GetSpan(r.Buffers[0])).ToArray();
        Assert.True(audio.Max(Math.Abs) > 0.05f);
        Assert.Contains(progress, p => p.Stage == "voz");

        ModuleResult big = await module.Client.InvokeAsync("voz", Json("""{"text":"Sin búfer.","language":"es"}"""));
        Assert.False(big.Value!.Value.GetProperty("written").GetBoolean());
        string token = big.Value.Value.GetProperty("audio").GetString()!;
        long n = big.Value.Value.GetProperty("frames").GetInt64();
        SharedMemoryRegion second = module.Client.CreateRegion(n * 4);
        ModuleResult delivered = await module.Client.InvokeAsync("entregar", Json($$"""{"audio":"{{token}}"}"""), [second.Describe(0, n * 4)]);
        Assert.Equal(n, delivered.Buffers.Single().Audio!.Frames);
        await Assert.ThrowsAsync<ModuleCallException>(() => module.Client.InvokeAsync("entregar", Json($$"""{"audio":"{{token}}"}"""), [second.Describe(0, n * 4)]));

        Process p = Process.GetProcessById(module.ProcessId);
        p.Refresh();
        output.WriteLine($"Memoria del módulo: {p.PeakWorkingSet64 / 1048576.0:F0} MB de pico; VRAM: 0 (solo CPU).");
    }

    [ModelFact]
    public async Task Cancelling_a_long_text_is_confirmed_quickly_and_the_module_keeps_working()
    {
        await using ModuleProcess module = await ModuleProcess.StartAsync(Exe);
        string text = string.Join(" ", Enumerable.Repeat("Rodaje es un editor de video para Windows; corta, ajusta el color y exporta en segundos.", 15));
        using CancellationTokenSource cts = new();
        Task call = module.Client.InvokeAsync("voz", Json(JsonSerializer.Serialize(new Dictionary<string, string> { ["text"] = text, ["language"] = "es" },
            VoiceTestJson.Default.DictionaryStringString)), cancellationToken: cts.Token);
        await Task.Delay(800);
        Stopwatch watch = Stopwatch.StartNew();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        output.WriteLine($"Cancelación confirmada en {watch.ElapsedMilliseconds} ms.");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1));
        ModuleResult again = await module.Client.InvokeAsync("voz", Json("""{"text":"Sigo aquí.","language":"es"}"""));
        Assert.True(again.Value!.Value.GetProperty("frames").GetInt64() > 0);
        Assert.False(module.Exited.IsCompleted);
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class VoiceTestJson : System.Text.Json.Serialization.JsonSerializerContext;
