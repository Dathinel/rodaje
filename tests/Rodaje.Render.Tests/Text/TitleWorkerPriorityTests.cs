using System.Diagnostics;
using System.Globalization;
using Rodaje.Media.Gpu;
using Rodaje.Playback.Composition;
using Rodaje.Render.Text;
using Rodaje.TestMedia;
using Xunit.Abstractions;

namespace Rodaje.Render.Tests.Text;

/// <summary>
/// El hilo de títulos corre a prioridad normal: solo trabaja cuando la vista (o la exportación) espera un título, y lo hace
/// con el candado del contexto de la GPU que también toma el hilo de render. Con la CPU llena de hilos normales, uno debajo
/// de lo normal casi no corre.
/// </summary>
public sealed class TitleWorkerPriorityTests(ITestOutputHelper output)
{
    [Fact]
    public void The_title_worker_runs_at_normal_priority_by_default()
    {
        Assert.Equal(ThreadPriority.Normal, new TitleRasterizerOptions().WorkerPriority);
    }

    [GpuFact]
    public void The_worker_thread_takes_the_priority_of_the_options()
    {
        using GpuDevice gpu = GpuDevice.Create();
        using (var titles = new TitleRasterizer(gpu))
        {
            Assert.Equal(ThreadPriority.Normal, titles.WorkerThreadPriority);
        }

        using (var low = new TitleRasterizer(gpu, new TitleRasterizerOptions { WorkerPriority = ThreadPriority.BelowNormal }))
        {
            Assert.Equal(ThreadPriority.BelowNormal, low.WorkerThreadPriority);
        }

        using var none = new TitleRasterizer(gpu, new TitleRasterizerOptions { BackgroundThread = false });
        Assert.Null(none.WorkerThreadPriority);
    }

    /// <summary>
    /// Mide cuánto tarda en llegar un título pedido con <see cref="TitleRasterizer.Prepare"/> con la CPU llena de hilos
    /// normales que no ceden, a prioridad normal y debajo de lo normal. Solo se exige que a prioridad normal llegue (el
    /// tope es contra cuelgues, no una medida de rendimiento); las cifras quedan en la salida de la prueba.
    /// </summary>
    [GpuFact]
    public void Under_full_cpu_a_new_title_still_arrives_at_the_default_priority()
    {
        using GpuDevice gpu = GpuDevice.Create();
        using var load = new CpuLoad(Environment.ProcessorCount * 2);
        TimeSpan?[] normal = Measure(gpu, ThreadPriority.Normal, titles: 5, TimeSpan.FromSeconds(30), "normal");
        TimeSpan?[] below = Measure(gpu, ThreadPriority.BelowNormal, titles: 2, TimeSpan.FromSeconds(8), "bajo");
        load.Dispose();

        output.WriteLine($"Procesadores: {Environment.ProcessorCount}; hilos de carga: {load.Threads}.");
        output.WriteLine($"Normal (ms): {Format(normal)}");
        output.WriteLine($"Debajo de lo normal (ms; tope de 8 s): {Format(below)}");
        Assert.All(normal, latency => Assert.True(latency.HasValue, "Un título a prioridad normal no llegó en 30 s con la CPU llena."));
    }

    private static TimeSpan?[] Measure(GpuDevice gpu, ThreadPriority priority, int titles, TimeSpan cap, string tag)
    {
        using var rasterizer = new TitleRasterizer(gpu, new TitleRasterizerOptions { WorkerPriority = priority });
        using var changed = new SemaphoreSlim(0);
        rasterizer.Changed += (_, _) => changed.Release();
        var latencies = new TimeSpan?[titles];
        for (int i = 0; i < titles; i++)
        {
            GeneratedContent content = TitleImage.Content($"Prioridad {tag} {i}", TitleRasterizerTests.Style(fill: "#FFFFFF"));
            var clock = Stopwatch.StartNew();
            Assert.False(rasterizer.Prepare(content));
            latencies[i] = changed.Wait(cap) ? clock.Elapsed : null;
        }

        return latencies;
    }

    private static string Format(TimeSpan?[] latencies) =>
        string.Join(", ", latencies.Select(l => l is { } t ? t.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture) : "no llegó"));

    /// <summary>Hilos normales que giran sin ceder hasta que se libera.</summary>
    private sealed class CpuLoad : IDisposable
    {
        private readonly Thread[] _threads;
        private volatile bool _stop;

        public CpuLoad(int threads)
        {
            _threads = new Thread[threads];
            for (int i = 0; i < threads; i++)
            {
                _threads[i] = new Thread(Spin) { IsBackground = true, Name = $"Carga {i + 1}", Priority = ThreadPriority.Normal };
                _threads[i].Start();
            }
        }

        public int Threads => _threads.Length;

        public void Dispose()
        {
            _stop = true;
            foreach (Thread thread in _threads)
            {
                thread.Join();
            }
        }

        private void Spin()
        {
            long n = 0;
            while (!_stop)
            {
                n++;
            }

            GC.KeepAlive(n);
        }
    }
}
