using Rodaje.Modules.Protocol;

namespace Rodaje.Modules.Protocol.Tests;

/// <summary>
/// Lo que cuelga de <see cref="ModuleClient.Completion"/> (en <see cref="ModuleProcess"/>, matar el árbol de procesos) no
/// corre en línea con <c>Fail</c>: las llamadas en curso fallan aunque esa continuación tarde.
/// </summary>
public sealed class FailPathTests
{
    [Fact]
    public async Task Fail_does_not_wait_for_a_slow_continuation_on_completion()
    {
        (ModuleClient client, Task<bool> host) = await Support.ConnectInProcessAsync();
        using ManualResetEventSlim gate = new(false);
        try
        {
            // Como el Kill de ModuleProcess, pero que no termina hasta que la prueba lo suelte.
            Task slow = client.Completion.ContinueWith(_ => gate.Wait(TimeSpan.FromSeconds(60)), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            Task<ModuleResult> call = client.InvokeAsync("contar", Support.Json("""{"pasos":10000,"msPorPaso":10}"""));
            await Task.Delay(50);

            // Fail corre en un hilo propio. Con Task.Run corría en el pool y las dos continuaciones que suelta (la de la
            // llamada y la lenta) iban a la cola local de ese hilo; al terminar Fail, el hilo tomaba primero la lenta (la
            // cola local es LIFO) y se quedaba bloqueado en ella con la de la llamada todavía en su cola, que solo otro
            // hilo libre podía robar: en la CI de Windows, sin hilos libres, la llamada no terminó en 10 s. Lo que se
            // prueba es que Fail no espera a la continuación lenta, no cómo reparte el pool; desde un hilo que no es del
            // pool las dos van a la cola global.
            Task fail = Task.Factory.StartNew(() => client.Fail(new ModuleDisconnectedException("Corte de prueba.")), CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
            await fail.WaitAsync(Support.CiBound);
            await Assert.ThrowsAsync<ModuleDisconnectedException>(() => call.WaitAsync(Support.CiBound));
            Assert.False(slow.IsCompleted);
        }
        finally
        {
            gate.Set();
            await client.DisposeAsync();
            await host.WaitAsync(TimeSpan.FromSeconds(60));
        }
    }
}
