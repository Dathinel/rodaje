using Rodaje.Modules.Protocol;

namespace Rodaje.Modules.Protocol.Tests;

/// <summary>
/// Lo que cuelga de <see cref="ModuleClient.Completion"/> (en <c>ModuleConnection</c>, matar el árbol de procesos) no
/// corre en línea con <c>Fail</c> ni delante de las llamadas: las llamadas en curso fallan aunque esa continuación tarde.
/// </summary>
public sealed class FailPathTests
{
    /// <summary>
    /// Fail en un hilo del pool, como en producción (el bucle de lectura, la salida del proceso). Las continuaciones que
    /// suelta (la de la llamada y la lenta) van a la cola local de ese hilo, que al terminar Fail toma primero la última
    /// (LIFO); otro hilo solo puede robar la primera. Si la lenta era la última, el hilo se bloqueaba en ella con la de la
    /// llamada todavía en su cola: en la CI de Windows, sin hilos libres, la llamada no terminó en 10 s. Fail suelta ahora
    /// la de Completion antes que las de las llamadas, así que, si la lenta acaba en el mismo hilo que Fail, la llamada ya
    /// tiene que haber terminado. Con hilos libres otro suele robar la llamada a tiempo y el defecto solo se ve en algunas
    /// vueltas: por eso se repite.
    /// </summary>
    [Fact]
    public async Task Fail_does_not_wait_for_a_slow_continuation_on_completion()
    {
        for (int round = 0; round < 20; round++)
        {
            bool? callDone = await FailFromAPoolThreadAsync();
            Assert.True(callDone != false, $"Vuelta {round}: la continuación lenta corrió en el hilo de Fail antes que la llamada.");
        }
    }

    /// <returns>Si la continuación lenta corrió en el hilo de Fail, si la llamada ya había terminado; si no, null.</returns>
    private static async Task<bool?> FailFromAPoolThreadAsync()
    {
        (ModuleClient client, Task<bool> host) = await Support.ConnectInProcessAsync();
        using ManualResetEventSlim gate = new(false);
        try
        {
            Task<ModuleResult>? call = null;
            int failThread = 0;
            bool? callDoneWhenSlowStartedOnFailThread = null;
            TaskCompletionSource slowStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            // Como el OnExited de ModuleConnection (mata el árbol de procesos), pero que no termina hasta que la prueba lo
            // suelte.
            Task slow = client.Completion.ContinueWith(_ =>
            {
                if (Environment.CurrentManagedThreadId == Volatile.Read(ref failThread))
                    callDoneWhenSlowStartedOnFailThread = call!.IsCompleted;
                slowStarted.TrySetResult();
                gate.Wait(TimeSpan.FromSeconds(60));
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            call = client.InvokeAsync("contar", Support.Json("""{"pasos":10000,"msPorPaso":10}"""));
            await Task.Delay(50);

            Task fail = Task.Run(() =>
            {
                Volatile.Write(ref failThread, Environment.CurrentManagedThreadId);
                client.Fail(new ModuleDisconnectedException("Corte de prueba."));
            });
            await fail.WaitAsync(Support.CiBound);
            await Assert.ThrowsAsync<ModuleDisconnectedException>(() => call.WaitAsync(Support.CiBound));
            Assert.False(slow.IsCompleted);
            await slowStarted.Task.WaitAsync(Support.CiBound);
            return callDoneWhenSlowStartedOnFailThread;
        }
        finally
        {
            gate.Set();
            await client.DisposeAsync();
            await host.WaitAsync(TimeSpan.FromSeconds(60));
        }
    }
}
