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
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                RunLoop(body, ref failure);
            }
            finally
            {
                finished.TrySetResult();
            }
        })
        { IsBackground = true, Name = "Hilo de interfaz de prueba" };
        thread.Start();

        // xUnit 2.9 (algoritmo «conservative») corre el cuerpo de las pruebas en hilos del pool, así que esta espera ocupa
        // uno mientras dura la prueba. Con Thread.Join el pool no se enteraba: cuatro pruebas así a la vez dejaban el pool
        // en su mínimo y lo demás esperaba a que la inyección lenta de hilos sumara uno. Esperar una Task desde un hilo
        // del pool hace que el pool lo cuente como bloqueado y ponga otro en su lugar enseguida.
        if (!finished.Task.Wait(timeout))
        {
            throw new TimeoutException($"La prueba no terminó en {timeout.TotalSeconds:0} s. {PoolState()}");
        }

        failure?.Throw();
    }

    private static void RunLoop(Func<Task> body, ref ExceptionDispatchInfo? failure)
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

        // Se cierra el bucle en el hilo que termina la tarea (casi siempre este mismo), sin pasar por el pool: con
        // TaskScheduler.Default a secas el cierre era un trabajo más en la cola del pool, y en la CI, con el pool
        // ocupado por otras pruebas («Pool: 9 hilos, 1 trabajos en cola»), pruebas que no esperan nada
        // (TemplateDeleteConfirmTests, ClipPropertyEditorTests, AutosaveRecoveryCancelTests) agotaban los 120 s.
        task.ContinueWith(static (_, state) => ((LoopContext)state!).Complete(), context, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        context.RunUntilComplete();
        if (task.Exception is { } error)
        {
            failure = ExceptionDispatchInfo.Capture(error.InnerExceptions.Count == 1 ? error.InnerExceptions[0] : error);
        }
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

    /// <summary>
    /// Continuaciones que llegaron después de cerrar el hilo (por ejemplo, la de un <c>await</c> que la prueba dejó sin
    /// esperar) y corrieron en el pool. Antes tumbaban el proceso de pruebas entero («Test Run Aborted»).
    /// </summary>
    public static int LatePosts => _latePosts;

    private static int _latePosts;

    private sealed class LoopContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = [];
        private readonly Lock _gate = new();
        private bool _closed;

        public override void Post(SendOrPostCallback d, object? state)
        {
            lock (_gate)
            {
                if (!_closed)
                {
                    _queue.Add((d, state));
                    return;
                }
            }

            // El hilo ya cerró: se corre en el pool para que la continuación termine y no tumbe el proceso.
            Interlocked.Increment(ref _latePosts);
            System.Diagnostics.Trace.WriteLine($"UiThread: continuación tardía tras cerrar el hilo ({d.Method.DeclaringType}.{d.Method.Name}); va al pool.");
            ThreadPool.QueueUserWorkItem(static s => s.d(s.state), (d, state), preferLocal: false);
        }

        public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException();

        public void Complete()
        {
            lock (_gate)
            {
                _closed = true;
                _queue.CompleteAdding();
            }
        }

        public void RunUntilComplete()
        {
            foreach ((SendOrPostCallback callback, object? state) in _queue.GetConsumingEnumerable())
            {
                callback(state);
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _closed = true;
                _queue.CompleteAdding();
            }

            _queue.Dispose();
        }
    }
}
