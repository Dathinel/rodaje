using System.Collections.Concurrent;
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
        var errors = new ConcurrentQueue<Exception>();
        TimeSpan?[] normal;
        TimeSpan?[] below;
        int threads;
        var load = new CpuLoad(Environment.ProcessorCount * 2, errors);
        try
        {
            threads = load.Threads;
            normal = Measure(gpu, ThreadPriority.Normal, titles: 5, TimeSpan.FromSeconds(30), "normal", errors);
            below = Measure(gpu, ThreadPriority.BelowNormal, titles: 2, TimeSpan.FromSeconds(8), "bajo", errors);
        }
        finally
        {
            // Los hilos de carga terminan y se esperan pase lo que pase.
            load.Dispose();
        }

        output.WriteLine($"Procesadores: {Environment.ProcessorCount}; hilos de carga: {threads}.");
        output.WriteLine($"Normal (ms): {Format(normal)}");
        output.WriteLine($"Debajo de lo normal (ms; tope de 8 s): {Format(below)}");
        Assert.Empty(errors);
        Assert.All(normal, latency => Assert.True(latency.HasValue, "Un título a prioridad normal no llegó en 30 s con la CPU llena."));
    }

    /// <summary>
    /// El semáforo vive más que el rasterizador: un título que no llegó dentro del tope (habitual debajo de lo normal) se
    /// termina de dibujar después y avisa con <see cref="TitleRasterizer.Changed"/> desde el hilo de títulos. Antes el
    /// semáforo se liberaba primero (orden inverso de los <c>using</c>) y ese aviso tardío lanzaba
    /// <see cref="ObjectDisposedException"/> en el hilo de títulos, que tumbaba el proceso de pruebas. Ahora el rasterizador
    /// se libera antes (su <c>Dispose</c> espera al hilo de títulos) y cualquier excepción del aviso se recoge en vez de
    /// escapar del hilo.
    /// </summary>
    private static TimeSpan?[] Measure(
        GpuDevice gpu, ThreadPriority priority, int titles, TimeSpan cap, string tag, ConcurrentQueue<Exception> errors)
    {
        using var changed = new SemaphoreSlim(0);
        var latencies = new TimeSpan?[titles];
        var rasterizer = new TitleRasterizer(gpu, new TitleRasterizerOptions { WorkerPriority = priority });
        try
        {
            rasterizer.Changed += (_, _) =>
            {
                try
                {
                    changed.Release();
                }
                catch (Exception error)
                {
                    errors.Enqueue(error);
                }
            };

            for (int i = 0; i < titles; i++)
            {
                GeneratedContent content = TitleImage.Content($"Prioridad {tag} {i}", TitleRasterizerTests.Style(fill: "#FFFFFF"));
                var clock = Stopwatch.StartNew();
                Assert.False(rasterizer.Prepare(content));
                latencies[i] = changed.Wait(cap) ? clock.Elapsed : null;
            }
        }
        finally
        {
            // Espera al hilo de títulos antes de que se libere el semáforo.
            rasterizer.Dispose();
        }

        return latencies;
    }

    private static string Format(TimeSpan?[] latencies) =>
        string.Join(", ", latencies.Select(l => l is { } t ? t.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture) : "no llegó"));

    /// <summary>Hilos normales que giran sin ceder hasta que se libera; lo que lancen se recoge, no sale del hilo.</summary>
    private sealed class CpuLoad : IDisposable
    {
        private readonly Thread[] _threads;
        private readonly ConcurrentQueue<Exception> _errors;
        private volatile bool _stop;

        public CpuLoad(int threads, ConcurrentQueue<Exception> errors)
        {
            _errors = errors;
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
            try
            {
                long n = 0;
                while (!_stop)
                {
                    n++;
                }

                GC.KeepAlive(n);
            }
            catch (Exception error)
            {
                _errors.Enqueue(error);
            }
        }
    }
}
