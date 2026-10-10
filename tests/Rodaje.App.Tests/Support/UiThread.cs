using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;

namespace Rodaje.App.Tests.Support;

/// <summary>
/// Un hilo con su <see cref="SynchronizationContext"/> de un solo hilo, como el de la interfaz: las continuaciones de
/// <c>await</c> vuelven a él, así EditorSession se prueba con su afinidad de hilo real.
/// </summary>
internal static class UiThread
{
    public static void Run(Func<Task> body)
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
        if (!thread.Join(TimeSpan.FromSeconds(30)))
        {
            throw new TimeoutException("La prueba no terminó en 30 s.");
        }

        failure?.Throw();
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
