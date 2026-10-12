using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;

namespace Rodaje.App.Tests.Support;

/// <summary>
/// Un hilo con su <see cref="SynchronizationContext"/> de un solo hilo, como el de la interfaz: las continuaciones de
/// <c>await</c> vuelven a él, así EditorSession se prueba con su afinidad de hilo real.
/// </summary>
internal static class UiThread
{
    /// <summary>
    /// Tope contra cuelgues, no medida de tiempo. Con 30 s fallaban en la CI pruebas de milisegundos: el runner de Windows
    /// corre a la vez todos los proyectos de prueba en 4 núcleos y las continuaciones esperan al pool.
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);

    public static void Run(Func<Task> body) => Run(body, DefaultTimeout);

    public static void Run(Func<Task> body, TimeSpan timeout)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            using var context = new LoopContext();
            SynchronizationContext.SetSynchronizationContext(context);
            Task task;
            try
            {
                task = body();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
                return;
            }

            task.ContinueWith(_ => context.Complete(), TaskScheduler.Default);
            context.RunUntilComplete();
            if (task.Exception is { } error)
            {
                failure = ExceptionDispatchInfo.Capture(error.InnerExceptions.Count == 1 ? error.InnerExceptions[0] : error);
            }
        })
        { IsBackground = true, Name = "Hilo de interfaz de prueba" };
        thread.Start();
        if (!thread.Join(timeout))
        {
            throw new TimeoutException($"La prueba no terminó en {timeout.TotalSeconds:0} s. {PoolState()}");
        }

        failure?.Throw();
    }

    /// <summary>Estado del pool de hilos, para saber si una espera agotada fue por falta de hilos.</summary>
    public static string PoolState()
    {
        ThreadPool.GetMinThreads(out int min, out _);
        return $"Pool: {ThreadPool.ThreadCount} hilos (mínimo {min}), {ThreadPool.PendingWorkItemCount} trabajos en cola; {Environment.ProcessorCount} núcleos.";
    }

    public static void Run(Action body) => Run(() =>
    {
        body();
        return Task.CompletedTask;
    });

    private sealed class LoopContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = [];

        public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

        public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException();

        public void Complete() => _queue.CompleteAdding();

        public void RunUntilComplete()
        {
            foreach ((SendOrPostCallback callback, object? state) in _queue.GetConsumingEnumerable())
            {
                callback(state);
            }
        }

        public void Dispose() => _queue.Dispose();
    }
}
