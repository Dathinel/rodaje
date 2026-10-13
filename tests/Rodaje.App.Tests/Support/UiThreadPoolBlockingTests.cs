using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using Xunit.Abstractions;

namespace Rodaje.App.Tests.Support;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class UiThreadPoolBlockingGroup
{
    public const string Name = "Pool de hilos sin otras pruebas a la vez";
}

/// <summary>
/// Gemela sin reloj de <see cref="UiThreadPoolTests"/>, para la CI: no mide cuánto tardan en empezar, sino si el pool
/// se entera de que <see cref="UiThread.Run(Func{Task})"/> bloquea su hilo. Esperando una Task desde un hilo del pool,
/// el pool lo cuenta como bloqueado y sube su meta de hilos hacia «mínimo + bloqueados», con un ajuste de motivo
/// «bloqueo cooperativo» que publica el evento <c>ThreadPoolWorkerThreadAdjustmentAdjustment</c> del runtime. Con
/// <c>Thread.Join</c> el pool no sabe nada y ese ajuste no llega nunca: lo único que suma hilos entonces es la inanición,
/// que tiene otro motivo.
/// </summary>
/// <remarks>
/// Corre sin otras pruebas a la vez (las de su colección van al final, solas): una prueba ajena que espere una Task
/// también pide ajustes por bloqueo y taparía la vuelta a <c>Thread.Join</c>.
/// </remarks>
[Collection(UiThreadPoolBlockingGroup.Name)]
public sealed class UiThreadPoolBlockingTests(ITestOutputHelper output)
{
    [Fact]
    public async Task El_pool_cuenta_como_bloqueado_el_hilo_que_espera_a_la_prueba_de_interfaz()
    {
        ThreadPool.GetMinThreads(out int min, out _);
        // La meta del pool por bloqueos es «mínimo + bloqueados» y solo sube si estaba por debajo; si otras pruebas la
        // dejaron alta, se bloquean más para pasarla (la meta no queda por encima de los hilos que existen).
        int tests = Math.Clamp(ThreadPool.ThreadCount - min + 8, 8, 64);
        int target = min + tests;
        using var listener = new PoolAdjustmentListener { Target = target };
        int started = 0;
        TaskCompletionSource allStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task[] runs = [.. Enumerable.Range(0, tests).Select(_ => Task.Run(() => UiThread.Run(async () =>
        {
            if (Interlocked.Increment(ref started) == tests)
            {
                allStarted.TrySetResult();
            }

            await release.Task;
        })))];
        bool reached;
        try
        {
            // Topes contra cuelgues, no medidas: en verde vuelve en cuanto el pool sube su meta por los bloqueados.
            await allStarted.Task.WaitAsync(TimeSpan.FromSeconds(120));
            reached = await Task.WhenAny(listener.ReachedByBlocking, Task.Delay(TimeSpan.FromSeconds(60))) == listener.ReachedByBlocking;
        }
        finally
        {
            release.TrySetResult();
        }

        await Task.WhenAll(runs).WaitAsync(TimeSpan.FromSeconds(120));
        output.WriteLine($"{tests} pruebas de interfaz bloqueadas; ajustes del pool: {listener.Describe()}. {UiThread.PoolState()}");
        Assert.True(reached,
            $"Con {tests} pruebas de interfaz bloqueadas el pool no subió su meta por bloqueo a {target} hilos (mínimo {min} + {tests}): "
            + $"UiThread.Run bloquea sin que el pool lo sepa (¿volvió Thread.Join?). Ajustes: {listener.Describe()}. {UiThread.PoolState()}");
    }

    /// <summary>Escucha los ajustes de la meta de hilos del pool (eventos del runtime, palabra clave de hilos).</summary>
    private sealed class PoolAdjustmentListener : EventListener
    {
        // ThreadingKeyword del proveedor Microsoft-Windows-DotNETRuntime y el motivo «CooperativeBlocking» del ajuste (HillClimbing.StateOrTransition: Starvation es 6).
        private const EventKeywords Threading = (EventKeywords)0x10000;
        private const uint CooperativeBlocking = 8;

        private readonly ConcurrentQueue<(uint Goal, uint Reason)> _adjustments = new();

        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Se completa con el primer ajuste por bloqueo que lleva la meta a <see cref="Target"/> o más.</summary>
        public Task ReachedByBlocking => _reached.Task;

        public int Target { get; set; } = int.MaxValue;

        public string Describe() => _adjustments.IsEmpty ? "ninguno"
            : string.Join(", ", _adjustments.Take(40).Select(a => $"{a.Goal} ({(a.Reason == CooperativeBlocking ? "bloqueo" : "motivo " + a.Reason)})"));

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == "Microsoft-Windows-DotNETRuntime")
            {
                EnableEvents(eventSource, EventLevel.Informational, Threading);
            }
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (eventData.EventName != "ThreadPoolWorkerThreadAdjustmentAdjustment" || eventData.Payload is not { Count: >= 3 } payload)
            {
                return;
            }

            uint goal = Convert.ToUInt32(payload[1], System.Globalization.CultureInfo.InvariantCulture);
            uint reason = Convert.ToUInt32(payload[2], System.Globalization.CultureInfo.InvariantCulture);
            _adjustments.Enqueue((goal, reason));
            if (reason == CooperativeBlocking && goal >= Target)
            {
                _reached.TrySetResult();
            }
        }
    }
}
