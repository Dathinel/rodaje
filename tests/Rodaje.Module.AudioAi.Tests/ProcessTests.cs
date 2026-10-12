using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Rodaje.Modules.Protocol;
using Xunit.Abstractions;

namespace Rodaje.Module.AudioAi.Tests;

/// <summary>
/// El módulo como proceso aparte, por el protocolo (lo que hace la app): <c>RODAJE_MODULO_AUDIO_IA</c> apunta a otro exe,
/// por ejemplo el publicado con Native AOT.
/// </summary>
public class ProcessTests(ITestOutputHelper output)
{
    private static string Exe =>
        Environment.GetEnvironmentVariable("RODAJE_MODULO_AUDIO_IA") is { Length: > 0 } exe ? exe : Path.Combine(AppContext.BaseDirectory, "Rodaje.Module.AudioAi.exe");

    private static async Task<ModuleProcess> StartAsync()
    {
        // El módulo busca los modelos donde los dejó preparar-modelos.ps1.
        if (Corpus.Environment is { } environment)
        {
            Environment.SetEnvironmentVariable("RODAJE_ENTORNO", environment);
        }

        return await ModuleProcess.StartAsync(Exe);
    }

    private static SharedBufferRef Audio(ModuleProcess module, float[] samples, int rate, int channels, out SharedMemoryRegion region, out SharedBufferRef output)
    {
        long bytes = (long)samples.Length * sizeof(float);
        region = module.Client.CreateRegion(bytes * 2);
        MemoryMarshal.AsBytes(samples.AsSpan()).CopyTo(region.GetSpan(0, bytes));
        output = region.Describe(bytes, bytes, audio: new AudioLayout(rate, channels, samples.Length / channels));
        return region.Describe(0, bytes, audio: new AudioLayout(rate, channels, samples.Length / channels));
    }

    [Fact]
    public async Task Denoises_and_detects_voice_through_the_protocol()
    {
        if (!Corpus.HasModels || NoiseReductionTests.Voice(20) is not { } clean)
        {
            output.WriteLine(Corpus.Missing);
            return;
        }

        var watch = Stopwatch.StartNew();
        await using ModuleProcess module = await StartAsync();
        output.WriteLine($"Arranque y saludo: {watch.Elapsed.TotalMilliseconds:0} ms; funciones: {string.Join(", ", module.Client.Module.Functions.Select(f => f.Id))}");

        float[] noisy = Noise.Mix(clean, Noise.Make(Noise.Kind.Pink, clean.Length, 48_000, 2), 0);
        SharedBufferRef input = Audio(module, noisy, 48_000, 1, out SharedMemoryRegion region, out SharedBufferRef target);
        var stages = new List<double>();
        watch.Restart();
        ModuleResult result = await module.Client.InvokeAsync("ruido", null, [input, target], new Progress<ModuleProgress>(p => stages.Add(p.Fraction)));
        output.WriteLine($"ruido: 20 s en {watch.Elapsed.TotalMilliseconds:0} ms ({result.Value}), {stages.Count} avisos de avance");
        float[] enhanced = MemoryMarshal.Cast<byte, float>(region.GetSpan(result.Buffers[0])).ToArray();
        Assert.True(Noise.SiSnr(clean, enhanced) - Noise.SiSnr(clean, noisy) >= 9);

        float[] voice16 = [.. Enumerable.Range(0, clean.Length / 3).Select(i => clean[i * 3])];
        long windows = (voice16.Length + 511) / 512;
        SharedMemoryRegion vad = module.Client.CreateRegion((voice16.Length + windows) * sizeof(float));
        MemoryMarshal.AsBytes(voice16.AsSpan()).CopyTo(vad.GetSpan(0, voice16.Length * sizeof(float)));
        SharedBufferRef audio = vad.Describe(0, voice16.Length * sizeof(float), audio: new AudioLayout(16_000, 1, voice16.Length));
        SharedBufferRef probabilities = vad.Describe(voice16.Length * sizeof(float), windows * sizeof(float));
        ModuleResult detected = await module.Client.InvokeAsync("detectar-voz", null, [audio, probabilities]);
        float[] p = MemoryMarshal.Cast<byte, float>(vad.GetSpan(detected.Buffers[0])).ToArray();
        output.WriteLine($"detectar-voz: {detected.Value}; {p.Count(x => x > 0.5) * 100 / p.Length} % de ventanas con voz");
        Assert.Equal(windows, detected.Value!.Value.GetProperty("ventanas").GetInt64());
        Assert.True(p.Count(x => x > 0.5) > p.Length / 2, "La lectura de LibriVox es casi toda voz.");
    }

    [Fact]
    public async Task Cancel_is_confirmed_quickly_and_the_module_keeps_working()
    {
        if (!Corpus.HasModels || NoiseReductionTests.Voice(180) is not { } clean)
        {
            output.WriteLine(Corpus.Missing);
            return;
        }

        await using ModuleProcess module = await StartAsync();
        SharedBufferRef input = Audio(module, clean, 48_000, 1, out _, out SharedBufferRef target);
        var times = new List<double>();
        for (int i = 0; i < 5; i++)
        {
            using var cancel = new CancellationTokenSource();
            var started = new TaskCompletionSource();
            Task<ModuleResult> call = module.Client.InvokeAsync("ruido", null, [input, target],
                new Progress<ModuleProgress>(_ => started.TrySetResult()), cancel.Token);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await Task.Delay(50 + (i * 70));
            var watch = Stopwatch.StartNew();
            await cancel.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
            times.Add(watch.Elapsed.TotalMilliseconds);
        }

        output.WriteLine($"Cancelación confirmada: mediana {times.Order().ElementAt(2):0} ms, máximo {times.Max():0} ms");
        Assert.True(times.Max() < 1000);
        Assert.True((await module.Client.PingAsync()) < TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Bad_buffers_fail_the_call_without_closing_the_module()
    {
        if (!File.Exists(Exe))
        {
            return;
        }

        await using ModuleProcess module = await StartAsync();
        SharedBufferRef input = Audio(module, new float[16_000], 16_000, 1, out _, out SharedBufferRef target);
        ModuleCallException error = await Assert.ThrowsAsync<ModuleCallException>(() => module.Client.InvokeAsync("ruido", null, [input, target]));
        Assert.Equal(ErrorCodes.InvalidParameters, error.Code);
        using JsonDocument parameters = JsonDocument.Parse("""{"dispositivo":"cpu"}""");
        await Assert.ThrowsAsync<ModuleCallException>(() => module.Client.InvokeAsync("detectar-voz", parameters.RootElement, [input]));

        // Que siga abierto: contesta el ping (el tope es contra cuelgues). Lo que tarda en contestar no se mide aquí: en
        // la CI, con el runner cargado, pasó de 1 s; la latencia la mide Cancel_is_confirmed_quickly_and_the_module_keeps_working.
        await module.Client.PingAsync().WaitAsync(TimeSpan.FromSeconds(60));
        Assert.False(module.Exited.IsCompleted);
    }
}
