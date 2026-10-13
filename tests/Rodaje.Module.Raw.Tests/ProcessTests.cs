using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Rodaje.Modules.Protocol;
using Rodaje.TestMedia;
using Xunit.Abstractions;

namespace Rodaje.Module.Raw.Tests;

/// <summary>
/// El módulo como proceso aparte, por el protocolo (lo que hace la app). <c>RODAJE_MODULO_RAW</c> apunta a otro exe, por
/// ejemplo el publicado con Native AOT.
/// </summary>
public class ProcessTests(ITestOutputHelper output)
{
    private static string Exe =>
        Environment.GetEnvironmentVariable("RODAJE_MODULO_RAW") is { Length: > 0 } exe ? exe : Path.Combine(AppContext.BaseDirectory, "Rodaje.Module.Raw.exe");

    private static bool Ready => File.Exists(Exe) && DevelopTests.HasLibRaw;

    /// <summary>Una región con los archivos y una salida por archivo, en pares [archivo, salida].</summary>
    internal static List<SharedBufferRef> Pairs(ModuleClient client, IReadOnlyList<byte[]> files, int width, int height, string format, out SharedMemoryRegion region)
    {
        int frameBytes = width * height * PixelFormats.BytesPerPixel(format);
        long total = files.Sum(f => (long)f.Length + frameBytes);
        region = client.CreateRegion(total);
        var pairs = new List<SharedBufferRef>();
        long at = 0;
        foreach (byte[] file in files)
        {
            file.CopyTo(region.GetSpan(at, file.Length));
            pairs.Add(region.Describe(at, file.Length));
            at += file.Length;
            pairs.Add(region.Describe(at, frameBytes, new VideoLayout(width, height, width * PixelFormats.BytesPerPixel(format), format)));
            at += frameBytes;
        }

        return pairs;
    }

    [Fact]
    public async Task Info_and_develop_through_the_protocol()
    {
        if (!Ready)
        {
            output.WriteLine(DevelopTests.Missing);
            return;
        }

        const int Width = 480, Height = 240;
        var watch = Stopwatch.StartNew();
        await using ModuleProcess module = await ModuleProcess.StartAsync(Exe);
        output.WriteLine($"Arranque y saludo: {watch.Elapsed.TotalMilliseconds:0} ms");

        byte[] dng = SyntheticDng.Chart(Width, Height).Write();
        SharedMemoryRegion infoRegion = module.Client.CreateRegion(dng.Length);
        dng.CopyTo(infoRegion.GetSpan(0, dng.Length));
        ModuleResult info = await module.Client.InvokeAsync("info", null, [infoRegion.Describe(0, dng.Length)]);
        output.WriteLine($"info: {info.Value}");
        JsonElement value = info.Value!.Value;
        Assert.Equal(Width, value.GetProperty("ancho").GetInt32());
        Assert.Equal(24_000, value.GetProperty("cadencia").GetProperty("num").GetInt32());
        Assert.True(value.GetProperty("dng").GetBoolean());

        byte[][] frames = [.. Enumerable.Range(0, 4).Select(i => (SyntheticDng.Chart(Width, Height) with { FrameRate = null }).Write())];
        List<SharedBufferRef> pairs = Pairs(module.Client, frames, Width, Height, PixelFormats.Rgba16F, out SharedMemoryRegion region);
        using JsonDocument parameters = JsonDocument.Parse("""{"curva":"lineal","hilos":2}""");
        var progress = new List<double>();
        ModuleResult developed = await module.Client.InvokeAsync("revelar", parameters.RootElement, pairs, new Progress<ModuleProgress>(p => progress.Add(p.Fraction)));
        output.WriteLine($"revelar: {developed.Value}");
        Assert.Equal(4, developed.Buffers.Count);
        Half[] rgba = MemoryMarshal.Cast<byte, Half>(region.GetSpan(developed.Buffers[3])).ToArray();
        (int x, int y) = SyntheticDng.PatchCenter(3, Width, Height);
        (double r, double g, double b) = DevelopTests.Mean(rgba, Width, x, y);
        Assert.Equal(SyntheticDng.ChartPatches[3].B, b, 0.006);
        Assert.Equal(SyntheticDng.ChartPatches[3].R, r, 0.006);
        Assert.Equal(SyntheticDng.ChartPatches[3].G, g, 0.006);
    }

    [Fact]
    public async Task Bad_input_fails_the_call_without_closing_the_module()
    {
        if (!Ready)
        {
            output.WriteLine(DevelopTests.Missing);
            return;
        }

        await using ModuleProcess module = await ModuleProcess.StartAsync(Exe);
        byte[] dng = SyntheticDng.Chart(64, 32).Write();

        List<SharedBufferRef> wrongSize = Pairs(module.Client, [dng], 32, 32, PixelFormats.Bgra8, out _);
        ModuleCallException error = await Assert.ThrowsAsync<ModuleCallException>(() => module.Client.InvokeAsync("revelar", null, wrongSize));
        Assert.Equal(ErrorCodes.InvalidParameters, error.Code);
        output.WriteLine(error.Message);

        List<SharedBufferRef> garbage = Pairs(module.Client, [new byte[4096]], 64, 32, PixelFormats.Bgra8, out _);
        error = await Assert.ThrowsAsync<ModuleCallException>(() => module.Client.InvokeAsync("info", null, [garbage[0]]));
        Assert.Equal(ErrorCodes.InvalidParameters, error.Code);
        output.WriteLine(error.Message);

        List<SharedBufferRef> good = Pairs(module.Client, [dng], 64, 32, PixelFormats.Gray8, out _);
        error = await Assert.ThrowsAsync<ModuleCallException>(() => module.Client.InvokeAsync("revelar", null, good));
        Assert.Equal(ErrorCodes.InvalidParameters, error.Code);

        using JsonDocument bad = JsonDocument.Parse("""{"exposicion":"mucha"}""");
        List<SharedBufferRef> ok = Pairs(module.Client, [dng], 64, 32, PixelFormats.Bgra8, out _);
        error = await Assert.ThrowsAsync<ModuleCallException>(() => module.Client.InvokeAsync("revelar", bad.RootElement, ok));
        Assert.Equal(ErrorCodes.InvalidParameters, error.Code);

        ModuleResult result = await module.Client.InvokeAsync("revelar", null, ok);
        Assert.Single(result.Buffers);
        Assert.True((await module.Client.PingAsync()) < CiLimit);
    }

    /// <summary>
    /// Cotas de la CI para cancelar y para el ping: lejos de lo que tarda el revelado que no se cancela (ocho 1080p con un
    /// hilo) y del reloj fino, que el runner de Windows con todos los proyectos de prueba a la vez no cumple (en
    /// ModuleManagerTests, una espera de ping de 400 ms llegó a 2 063 ms de reloj). El segundo de cada una va en la gemela
    /// TiempoReal; el ping del final de Bad_input_fails_the_call_without_closing_the_module usa la misma cota de la CI.
    /// </summary>
    internal static readonly TimeSpan CiLimit = TimeSpan.FromSeconds(10);

    [Fact]
    public Task Cancel_is_confirmed_quickly() => CancelIsConfirmed(CiLimit);

    [Fact]
    [Trait("Categoria", "TiempoReal")]
    public Task Cancel_is_confirmed_within_a_second() => CancelIsConfirmed(TimeSpan.FromSeconds(1));

    private async Task CancelIsConfirmed(TimeSpan limit)
    {
        if (!Ready)
        {
            output.WriteLine(DevelopTests.Missing);
            return;
        }

        await using ModuleProcess module = await ModuleProcess.StartAsync(Exe);
        byte[] dng = SyntheticDng.Chart(1920, 1080).Write();
        List<SharedBufferRef> pairs = Pairs(module.Client, [.. Enumerable.Repeat(dng, 8)], 1920, 1080, PixelFormats.Rgba16F, out _);
        using JsonDocument one = JsonDocument.Parse("""{"hilos":1}""");
        using var cancel = new CancellationTokenSource();
        var started = new TaskCompletionSource();
        Task<ModuleResult> call = module.Client.InvokeAsync("revelar", one.RootElement, pairs, new Progress<ModuleProgress>(_ => started.TrySetResult()), cancel.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var watch = Stopwatch.StartNew();
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        output.WriteLine($"Cancelación confirmada en {watch.Elapsed.TotalMilliseconds:0} ms");
        Assert.True(watch.Elapsed < limit, $"La cancelación tardó {watch.Elapsed.TotalMilliseconds:0} ms (cota {limit.TotalSeconds:0} s).");
        TimeSpan ping = await module.Client.PingAsync();
        Assert.True(ping < limit, $"El ping tras cancelar tardó {ping.TotalMilliseconds:0} ms (cota {limit.TotalSeconds:0} s).");
    }
}
