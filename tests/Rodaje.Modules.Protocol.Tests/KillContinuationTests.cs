using Rodaje.Modules.Protocol;

namespace Rodaje.Modules.Protocol.Tests;

/// <summary>
/// La continuación de <see cref="ModuleProcess"/> que mata el proceso cuando el cliente falla corre en un hilo propio,
/// pero solo cuando hace falta: un cierre limpio no la programa (antes creaba un hilo en cada cierre).
/// </summary>
public sealed class KillContinuationTests
{
    [Fact]
    public async Task A_clean_close_does_not_schedule_the_kill()
    {
        CountingScheduler scheduler = new();
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int kills = 0;
        Task continuation = ModuleProcess.KillWhenFaulted(completion.Task, () => Interlocked.Increment(ref kills), scheduler);

        completion.SetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => continuation.WaitAsync(Support.CiBound));
        Assert.Equal(0, scheduler.Queued);
        Assert.Equal(0, kills);
    }

    [Fact]
    public async Task A_failure_kills_once_on_its_own_thread()
    {
        CountingScheduler scheduler = new();
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int kills = 0;
        bool? onPoolThread = null;
        Task continuation = ModuleProcess.KillWhenFaulted(completion.Task, () =>
        {
            onPoolThread = Thread.CurrentThread.IsThreadPoolThread;
            Interlocked.Increment(ref kills);
        }, scheduler);

        completion.SetException(new ModuleDisconnectedException("Corte de prueba."));

        await continuation.WaitAsync(Support.CiBound);
        Assert.Equal(1, scheduler.Queued);
        Assert.Equal(1, scheduler.LongRunning);
        Assert.Equal(1, kills);
        Assert.False(onPoolThread);
    }

    /// <summary>
    /// Regresión de la tarea 137: <see cref="ModuleProcess.DisposeAsync"/>, <see cref="ModuleProcess.Kill()"/> y el fallo
    /// del arranque mataban el árbol de procesos en línea, en un hilo del pool, durante los segundos que tarda con la CPU
    /// llena. Ahora lo hace <see cref="ModuleProcess.KillOffPool"/>: vuelve sin esperar y mata en un hilo propio. La
    /// muerte lenta se simula con una puerta que la prueba suelta, sin relojes.
    /// </summary>
    [Fact]
    public async Task Killing_returns_at_once_and_kills_on_its_own_thread()
    {
        CountingScheduler scheduler = new();
        TaskCompletionSource killStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using ManualResetEventSlim release = new(false);
        bool? onPoolThread = null;

        Task kill = ModuleProcess.KillOffPool(() =>
        {
            onPoolThread = Thread.CurrentThread.IsThreadPoolThread;
            killStarted.TrySetResult();
            // Bloquea a propósito, como Process.Kill(entireProcessTree): solo su hilo propio, nunca uno del pool.
            release.Wait();
        }, scheduler);

        try
        {
            await killStarted.Task.WaitAsync(Support.CiBound);
            Assert.False(kill.IsCompleted);
            Assert.Equal(1, scheduler.LongRunning);
            Assert.False(onPoolThread);
        }
        finally
        {
            release.Set();
        }
        await kill.WaitAsync(Support.CiBound);
        Assert.Equal(1, scheduler.Queued);
    }

    /// <summary>Cuenta lo que se le programa y lo corre en un hilo propio, como el planificador por defecto con LongRunning.</summary>
    private sealed class CountingScheduler : TaskScheduler
    {
        private int _queued;
        private int _longRunning;

        public int Queued => Volatile.Read(ref _queued);

        public int LongRunning => Volatile.Read(ref _longRunning);

        protected override void QueueTask(Task task)
        {
            Interlocked.Increment(ref _queued);
            if ((task.CreationOptions & TaskCreationOptions.LongRunning) != 0) Interlocked.Increment(ref _longRunning);
            new Thread(() => TryExecuteTask(task)) { IsBackground = true, Name = "Prueba: matar" }.Start();
        }

        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;

        protected override IEnumerable<Task> GetScheduledTasks() => [];
    }
}
