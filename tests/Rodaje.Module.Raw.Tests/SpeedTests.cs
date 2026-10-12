using System.Diagnostics;
using System.Text.Json;
using Rodaje.Modules.Protocol;
using Rodaje.TestMedia;
using Xunit.Abstractions;

namespace Rodaje.Module.Raw.Tests;

/// <summary>
/// Revelado de un CinemaDNG 4K DCI (4096×2160, 12 bits sin comprimir, 17,7 MB) por el
/// protocolo, como lo pide la app (el archivo copiado a la región, salida rgba16f), con un hilo y con todos.
/// </summary>
public class SpeedTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Categoria", "TiempoReal")]
    public async Task Develop_4k_speed()
    {
        string exe = Environment.GetEnvironmentVariable("RODAJE_MODULO_RAW") is { Length: > 0 } e ? e : Path.Combine(AppContext.BaseDirectory, "Rodaje.Module.Raw.exe");
        if (!File.Exists(exe) || !DevelopTests.HasLibRaw)
        {
            output.WriteLine(DevelopTests.Missing);
            return;
        }

        const int Width = 4096, Height = 2160;
        byte[] dng = SyntheticDng.Chart(Width, Height).Write();
        await using ModuleProcess module = await ModuleProcess.StartAsync(exe);
        foreach (string quality in (string[])["rapida", "alta"])
        {
            List<SharedBufferRef> single = ProcessTests.Pairs(module.Client, [dng], Width, Height, PixelFormats.Rgba16F, out _);
            using JsonDocument p1 = JsonDocument.Parse($$"""{"calidad":"{{quality}}"}""");
            await module.Client.InvokeAsync("revelar", p1.RootElement, single);
            var times = new List<double>();
            for (int i = 0; i < 4; i++)
            {
                var watch = Stopwatch.StartNew();
                await module.Client.InvokeAsync("revelar", p1.RootElement, single);
                times.Add(watch.Elapsed.TotalMilliseconds);
            }

            int batch = 8;
            List<SharedBufferRef> pairs = ProcessTests.Pairs(module.Client, [.. Enumerable.Repeat(dng, batch)], Width, Height, PixelFormats.Rgba16F, out _);
            var all = Stopwatch.StartNew();
            const int Rounds = 3;
            for (int i = 0; i < Rounds; i++)
            {
                await module.Client.InvokeAsync("revelar", p1.RootElement, pairs);
            }

            double fps = batch * Rounds / all.Elapsed.TotalSeconds;
            output.WriteLine($"4K DCI, calidad {quality}: un cuadro mediana {times.Order().ElementAt(2):0} ms (mín {times.Min():0}); "
                + $"lotes de {batch} con {Environment.ProcessorCount} hilos: {fps:0.0} cuadros/s");
        }
    }
}
