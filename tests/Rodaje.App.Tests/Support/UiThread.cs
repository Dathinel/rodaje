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
        (Task finished, Func<ExceptionDispatchInfo?> failure) = Start(body);

        // xUnit 2.9 (algoritmo «conservative») corre el cuerpo de las pruebas en hilos del pool, así que esta espera ocupa
        // uno mientras dura la prueba. Con Thread.Join el pool no se enteraba: cuatro pruebas así a la vez dejaban el pool
        // en su mínimo y lo demás esperaba a que la inyección lenta de hilos sumara uno. Esperar una Task desde un hilo
        // del pool hace que el pool lo cuente como bloqueado y ponga otro en su lugar enseguida.
        if (!finished.Wait(timeout))
        {
            throw new TimeoutException($"La prueba no terminó en {timeout.TotalSeconds:0} s. {PoolState()}");
        }

        failure()?.Throw();
    }

    /// <summary>
    /// Como <see cref="Run(Func{Task}, TimeSpan)"/>, pero el tope es contra un cuerpo que deja de avanzar, no contra uno
    /// lento: falla si <paramref name="progress"/> no cambia en <paramref name="idle"/>. Para cuerpos largos hechos de
    /// tramos cortos (el fuzz de la sesión): con un tope total, un runner cargado que va lento pero avanza falla igual que
    /// un cuelgue de verdad, y el mensaje no los distingue. <paramref name="waitSlice"/> sustituye la espera de cada tramo
    /// de vigilancia (<c>Task.Wait(idle)</c>; devuelve si el cuerpo terminó), para probarla sin depender del reloj.
    /// </summary>
    public static void Run(Func<Task> body, Func<long> progress, TimeSpan idle, Func<Task, TimeSpan, bool>? waitSlice = null)
    {
        ArgumentNullException.ThrowIfNull(progress);
        waitSlice ??= static (task, timeout) => task.Wait(timeout);
        // Antes de arrancar: leído después, podía ver ya el primer avance y tomarlo por el punto de partida.
        long seen = progress();
        (Task finished, Func<ExceptionDispatchInfo?> failure) = Start(body);
        while (!waitSlice(finished, idle))
        {
            long now = progress();
            if (now == seen && !finished.IsCompleted)
            {
                throw new TimeoutException($"La prueba no avanzó en {idle.TotalSeconds:0.#} s. {PoolState()}");
            }

            seen = now;
        }

        failure()?.Throw();
    }

    /// <summary>Arranca el hilo; devuelve la tarea que termina con él y lo que lanzó el cuerpo (leído después de terminar).</summary>
    private static (Task Finished, Func<ExceptionDispatchInfo?> Failure) Start(Func<Task> body)
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
        return (finished.Task, () => failure);
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
