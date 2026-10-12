using System.Diagnostics;
using System.Globalization;
using Rodaje.Modules.Protocol;
using Xunit.Abstractions;

namespace Rodaje.Modules.Protocol.Tests;

/// <summary>El módulo de ejemplo como proceso de verdad.</summary>
public sealed class ProcessTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Echo_module_starts_answers_and_exits_cleanly()
    {
        Stopwatch watch = Stopwatch.StartNew();
        ModuleProcess module = await ModuleProcess.StartAsync(Support.EchoExe);
        output.WriteLine($"Arranque y saludo: {watch.ElapsedMilliseconds} ms");
        Assert.Equal("rodaje-eco", module.Client.Module.Id);
        Assert.Equal("{\"a\":1}", (await module.Client.InvokeAsync("eco", Support.Json("{\"a\":1}"))).Value!.Value.GetRawText());
        await module.DisposeAsync();
        Assert.Equal(0, await module.Exited.WaitAsync(TimeSpan.FromSeconds(3)));
    }

    /// <remarks>La cota de 1 s la mide <see cref="A_module_that_dies_mid_call_fails_the_call_within_a_second"/> (TiempoReal).</remarks>
    [Fact]
    public async Task A_module_that_dies_mid_call_fails_the_call_fast()
    {
        await using ModuleProcess module = await ModuleProcess.StartAsync(Support.EchoExe);
        await Assert.ThrowsAnyAsync<ModuleDisconnectedException>(() => module.Client.InvokeAsync("morir", Support.Json("{\"codigo\":3}")).WaitAsync(TimeSpan.FromSeconds(60)));
        Assert.Equal(3, await module.Exited.WaitAsync(TimeSpan.FromSeconds(60)));
        await Assert.ThrowsAnyAsync<ModuleDisconnectedException>(() => module.Client.InvokeAsync("eco"));
    }

    [Fact]
    [Trait("Categoria", "TiempoReal")]
    public async Task A_module_that_dies_mid_call_fails_the_call_within_a_second()
    {
        await using ModuleProcess module = await ModuleProcess.StartAsync(Support.EchoExe);
        Stopwatch watch = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<ModuleDisconnectedException>(() => module.Client.InvokeAsync("morir", Support.Json("{\"codigo\":3}")));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"tardó {watch.ElapsedMilliseconds} ms");
        Assert.Equal(3, await module.Exited.WaitAsync(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task A_killed_module_fails_the_running_call()
    {
        await using ModuleProcess module = await ModuleProcess.StartAsync(Support.EchoExe);
        Task call = module.Client.InvokeAsync("contar", Support.Json("""{"pasos":10000,"msPorPaso":10}"""));
        await Task.Delay(100);
        Process.GetProcessById(module.ProcessId).Kill();
        await Assert.ThrowsAnyAsync<ModuleDisconnectedException>(() => call.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    /// <remarks>
    /// Comprueba que el módulo que no confirma la cancelación se mata y la llamada termina cancelada. Cuánto tarda lo mide
    /// <see cref="A_module_that_ignores_cancel_is_killed_soon_after_the_grace_period"/> (TiempoReal): con el runner de
    /// Windows cargado, matar el árbol de procesos llegó a tardar 4,6 s y esta prueba fallaba sin defecto.
    /// </remarks>
    [Fact]
    public async Task A_module_that_ignores_cancel_is_killed_after_the_grace_period()
    {
        await using ModuleProcess module = await ModuleProcess.StartAsync(Support.EchoExe, options: new ModuleClientOptions { CancelGrace = TimeSpan.FromMilliseconds(300) });
        using CancellationTokenSource cts = new(TimeSpan.FromMilliseconds(100));
        Stopwatch watch = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => module.Client.InvokeAsync("colgar", cancellationToken: cts.Token).WaitAsync(TimeSpan.FromSeconds(60)));
        Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(300), $"no esperó la gracia: {watch.ElapsedMilliseconds} ms");
        await module.Exited.WaitAsync(TimeSpan.FromSeconds(60));
    }

    [Fact]
    [Trait("Categoria", "TiempoReal")]
    public async Task A_module_that_ignores_cancel_is_killed_soon_after_the_grace_period()
    {
        await using ModuleProcess module = await ModuleProcess.StartAsync(Support.EchoExe, options: new ModuleClientOptions { CancelGrace = TimeSpan.FromMilliseconds(300) });
        using CancellationTokenSource cts = new(TimeSpan.FromMilliseconds(100));
        Stopwatch watch = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => module.Client.InvokeAsync("colgar", cancellationToken: cts.Token));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1.5), $"tardó {watch.ElapsedMilliseconds} ms");
        await module.Exited.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    [Trait("Categoria", "TiempoReal")]
    public async Task Cancellation_in_a_real_process_is_fast()
    {
        await using ModuleProcess module = await ModuleProcess.StartAsync(Support.EchoExe);
        List<double> ms = [];
        for (int i = 0; i < 20; i++)
        {
            using CancellationTokenSource cts = new();
            Task call = module.Client.InvokeAsync("contar", Support.Json("""{"pasos":10000,"msPorPaso":5}"""), cancellationToken: cts.Token);
            await Task.Delay(20 + i);
            Stopwatch watch = Stopwatch.StartNew();
            await cts.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
            ms.Add(watch.Elapsed.TotalMilliseconds);
        }
        output.WriteLine($"Cancelación confirmada: mediana {Percentile(ms, 50):0.0} ms, máximo {ms.Max():0.0} ms");
        Assert.True(ms.Max() < 1000);
        await module.Client.InvokeAsync("eco");
    }

    [Fact]
    public async Task A_missing_executable_is_reported()
    {
        await Assert.ThrowsAsync<ModuleExitedException>(() => ModuleProcess.StartAsync(Path.Combine(AppContext.BaseDirectory, "no-existe.exe")));
    }

    [Fact]
    public async Task Started_by_hand_the_module_explains_itself_and_exits()
    {
        using Process process = Process.Start(new ProcessStartInfo(Support.EchoExe) { RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true })!;
        string stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.Equal(64, process.ExitCode);
        Assert.Contains("módulo de Rodaje", stderr, StringComparison.Ordinal);
    }

    /// <summary>Cifras del protocolo con el módulo en otro proceso.</summary>
    [Fact]
    [Trait("Categoria", "TiempoReal")]
    public async Task Latency_and_shared_memory_throughput()
    {
        await using ModuleProcess module = await ModuleProcess.StartAsync(Support.EchoExe);
        ModuleClient client = module.Client;
        for (int i = 0; i < 200; i++) await client.PingAsync();

        List<double> ping = [];
        for (int i = 0; i < 2000; i++) ping.Add((await client.PingAsync()).TotalMicroseconds);
        List<double> eco = [];
        System.Text.Json.JsonElement small = Support.Json("""{"texto":"hola","n":1}""");
        for (int i = 0; i < 2000; i++)
        {
            long t = Stopwatch.GetTimestamp();
            await client.InvokeAsync("eco", small);
            eco.Add(Stopwatch.GetElapsedTime(t).TotalMicroseconds);
        }

        const int frame = 1920 * 1080 * 4;
        SharedMemoryRegion video = client.CreateRegion(2L * frame);
        VideoLayout layout = new(1920, 1080, 1920 * 4, PixelFormats.Rgba8);
        SharedBufferRef input = video.Describe(0, frame, layout);
        SharedBufferRef outputFrame = video.Describe(frame, frame, layout);
        byte[] source = new byte[frame];
        new Random(3).NextBytes(source);
        for (int i = 0; i < 20; i++) await client.InvokeAsync("copiar", buffers: [input, outputFrame]);

        const int frames = 300;
        List<double> perFrame = [];
        Stopwatch total = Stopwatch.StartNew();
        for (int i = 0; i < frames; i++)
        {
            long t = Stopwatch.GetTimestamp();
            source[i] = (byte)i;
            source.CopyTo(video.GetSpan(input));
            await client.InvokeAsync("copiar", buffers: [input, outputFrame]);
            Assert.Equal((byte)i, video.GetSpan(outputFrame)[i]);
            perFrame.Add(Stopwatch.GetElapsedTime(t).TotalMilliseconds);
        }
        double fps = frames / total.Elapsed.TotalSeconds;
        Assert.True(video.GetSpan(outputFrame).SequenceEqual(source));

        List<double> inPlace = [];
        for (int i = 0; i < frames; i++)
        {
            long t = Stopwatch.GetTimestamp();
            await client.InvokeAsync("eco", buffers: [input]);
            inPlace.Add(Stopwatch.GetElapsedTime(t).TotalMilliseconds);
        }

        const int audioFrames = 48000;
        const int audioBytes = audioFrames * 2 * 4;
        SharedMemoryRegion audio = client.CreateRegion(2L * audioBytes);
        AudioLayout stereo = new(48000, 2, audioFrames);
        SharedBufferRef audioIn = audio.Describe(0, audioBytes, audio: stereo);
        SharedBufferRef audioOut = audio.Describe(audioBytes, audioBytes, audio: stereo);
        const int seconds = 600;
        Stopwatch audioWatch = Stopwatch.StartNew();
        for (int i = 0; i < seconds; i++) await client.InvokeAsync("copiar", buffers: [audioIn, audioOut]);
        double realtime = seconds / audioWatch.Elapsed.TotalSeconds;

        string Report(string name, List<double> v, string unit) =>
            string.Create(CultureInfo.InvariantCulture, $"{name}: p50 {Percentile(v, 50):0.0} {unit}, p99 {Percentile(v, 99):0.0} {unit}");
        output.WriteLine(Report("Ping", ping, "µs"));
        output.WriteLine(Report("Llamada eco (JSON pequeño)", eco, "µs"));
        output.WriteLine(Report("Cuadro 1080p RGBA: escribir + copiar en el módulo + leer", perFrame, "ms"));
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {fps:0} cuadros/s, {fps * frame * 3 / 1e9:0.00} GB/s movidos (escritura, copia y lectura)"));
        output.WriteLine(Report("Cuadro 1080p por referencia (sin tocar los datos)", inPlace, "ms"));
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Audio 48 kHz estéreo f32 en bloques de 1 s: {realtime:0}× tiempo real"));

        Assert.True(Percentile(ping, 50) < 1000, "ping p50 ≥ 1 ms");
        Assert.True(fps >= 60, $"1080p a {fps:0} fps");
        Assert.True(realtime >= 100, $"audio a {realtime:0}×");
    }

    private static double Percentile(List<double> values, double p)
    {
        List<double> sorted = [.. values.Order()];
        return sorted[(int)Math.Clamp(Math.Ceiling(p / 100 * sorted.Count) - 1, 0, sorted.Count - 1)];
    }
}
