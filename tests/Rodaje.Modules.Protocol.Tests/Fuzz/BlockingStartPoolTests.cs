using System.Collections.Concurrent;

namespace Rodaje.Modules.Protocol.Tests.Fuzz;

/// <summary>
/// Tarea 289: <see cref="ModuleHost.RunBlockingAsync{T}(Func{T}, CancellationToken)"/> arrancaba su hilo con
/// <c>StartNew(LongRunning)</c> en el hilo que la llama, y <see cref="Thread.Start()"/> retiene a ese hilo hasta que el hilo
/// nuevo arranca (en la CI de Windows esa espera pasó de 10 s; tareas 235, 260 y 282). Visión la llama en oleadas tras un
/// <c>await</c>, es decir, desde el pool: con muchas llamadas a la vez, los hilos del pool quedaban esperando arranques y el
/// bucle de <see cref="ModuleHost"/> no contestaba el <c>ping</c> ni mandaba los avances, y el vigilante mataba el módulo.
/// Es lo mismo que #249 arregló para los hilos de las llamadas y #262 para <c>FrameBatch</c> de RAW. La costura hace de
/// arranques que no terminan; es por hilo (<see cref="ThreadStaticAttribute"/>) y la lee el hilo que llama, así que solo
/// afecta a los arranques que pide cada prueba.
/// </summary>
[Collection(FuzzGroup.Name)]
public sealed class BlockingStartPoolTests
{
    /// <summary>Muchas más que los hilos del pool (32 como mínimo, véase el .csproj), así que no depende de cuántos inyecte.</summary>
    private const int Calls = 120;

    [Fact]
    public void A_call_from_the_pool_does_not_start_the_thread_on_the_pool() => RunFromPool();

    [Fact]
    public void The_ping_is_answered_while_waves_started_from_the_pool_are_slow_to_start() => RunWaves();

    /// <summary>Esperas bloqueantes y no Task.WaitAsync, como en CallStartPoolTests: su plazo corre aunque el pool esté sin hilos.</summary>
    private static void RunFromPool()
    {
        ConcurrentQueue<bool> onPool = new();
        StartGate gate = new();
        Countdown returned = new(Calls);
        Task[] work = new Task[Calls];
        try
        {
            for (int i = 0; i < Calls; i++)
            {
                int k = i;
                ThreadPool.UnsafeQueueUserWorkItem(_ =>
                {
                    ModuleHost.BeforeBlockingThreadStartsForTests = () =>
                    {
                        onPool.Enqueue(Thread.CurrentThread.IsThreadPoolThread);
                        gate.Wait();
                    };
                    try
                    {
                        work[k] = ModuleHost.RunBlockingAsync(() => Thread.CurrentThread.IsThreadPoolThread);
                    }
                    finally
                    {
                        ModuleHost.BeforeBlockingThreadStartsForTests = null;
                    }

                    returned.Signal();
                }, null);
            }

            Assert.True(returned.Wait(FuzzSupport.CaseCap),
                $"Colgado: {returned.Remaining} de {Calls} llamadas a RunBlockingAsync desde el pool no volvieron en {FuzzSupport.CaseCap.TotalSeconds:0} s con los arranques retenidos: el pool espera a Thread.Start.");
            Assert.DoesNotContain(true, onPool);
        }
        finally
        {
            gate.Open();
        }

        Assert.True(Task.WhenAll(work).Wait(FuzzSupport.CaseCap), "Los trabajos no terminaron tras soltar los arranques.");
        Assert.Equal(Calls, onPool.Count);
    }

    private static void RunWaves()
    {
        StartGate gate = new();
        Countdown reached = new(Calls);
        ConcurrentQueue<bool> continuedOnPool = new();
        ModuleHost host = new(new ModuleInfo("prueba-oleadas", "1.0.0", "Prueba oleadas", [new FunctionInfo("oleada", "Oleada")]))
        {
            MaxConcurrentCalls = Calls,
        };
        // Como «quitar fondo» en Visión: tras un «await», ya en el pool, lanza una oleada de trabajos que bloquean.
        host.Register("oleada", async (_, ct) =>
        {
            await Task.Yield();
            continuedOnPool.Enqueue(Thread.CurrentThread.IsThreadPoolThread);
            Task<int>[] wave;
            ModuleHost.BeforeBlockingThreadStartsForTests = gate.Wait;
            try
            {
                wave = [.. Enumerable.Range(0, 4).Select(i => ModuleHost.RunBlockingAsync(() => i, ct))];
            }
            finally
            {
                ModuleHost.BeforeBlockingThreadStartsForTests = null;
            }

            reached.Signal();
            await Task.WhenAll(wave).ConfigureAwait(false);
            return new ModuleOutput();
        });

        (ModuleClient client, Task<bool> run) = Support.ConnectInProcessAsync(host).GetAwaiter().GetResult();
        Task<ModuleResult>[] calls;
        try
        {
            calls = [.. Enumerable.Range(0, Calls).Select(_ => client.InvokeAsync("oleada"))];

            Assert.True(reached.Wait(FuzzSupport.CaseCap),
                $"Colgado: {reached.Remaining} de {Calls} llamadas no pasaron de lanzar su oleada en {FuzzSupport.CaseCap.TotalSeconds:0} s con los arranques retenidos.");
            Assert.DoesNotContain(false, continuedOnPool);
            Assert.True(client.PingAsync().Wait(FuzzSupport.CaseCap),
                $"El módulo no contestó el ping en {FuzzSupport.CaseCap.TotalSeconds:0} s con los arranques de las oleadas retenidos.");
        }
        finally
        {
            gate.Open();
        }

        Assert.True(Task.WhenAll(calls).Wait(FuzzSupport.CaseCap), "Las llamadas no terminaron tras soltar los arranques.");
        client.DisposeAsync().AsTask().Wait(FuzzSupport.CaseCap);
        Assert.True(run.Wait(FuzzSupport.CaseCap));
    }
}

/// <summary>
/// Una cuenta atrás sin nada que desechar: los trabajos retenidos pueden llegar a ella después de que la prueba falle y
/// termine (un <see cref="CountdownEvent"/> ya desechado tiraría el proceso de pruebas desde el pool).
/// </summary>
internal sealed class Countdown(int count)
{
    private readonly object _lock = new();

    public int Remaining
    {
        get
        {
            lock (_lock) return count;
        }
    }

    public void Signal()
    {
        lock (_lock)
        {
            if (--count <= 0) Monitor.PulseAll(_lock);
        }
    }

    public bool Wait(TimeSpan timeout)
    {
        long end = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        lock (_lock)
        {
            while (count > 0)
            {
                long left = end - Environment.TickCount64;
                if (left <= 0 || !Monitor.Wait(_lock, TimeSpan.FromMilliseconds(left))) return count <= 0;
            }

            return true;
        }
    }
}
