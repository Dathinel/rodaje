using System.Collections.Concurrent;
using Rodaje.Modules.Protocol;

namespace Rodaje.Modules.Protocol.Tests;

/// <summary>
/// El trabajo largo de un módulo no corre en el pool (tarea 195). Visión, voz y subtítulos inferían, sintetizaban o
/// cargaban el modelo bloqueando hilos del pool; con varias llamadas a la vez y la CPU llena el pool no sumaba otros, y el
/// bucle de <see cref="ModuleHost"/>, que lee y contesta con E/S asíncrona (sus continuaciones corren en el pool), no
/// contestaba el <c>ping</c>: el vigilante del editor mataba un módulo sano. «RAW de cine» ya lo hacía en un hilo propio
/// desde la tarea 138; ahora <see cref="ModuleHost"/> corre cada función en un hilo propio y da
/// <see cref="ModuleHost.RunBlockingAsync{T}(Func{T}, CancellationToken)"/> para el resto.
/// </summary>
/// <remarks>
/// Sin reloj ni número de hilos: llenar el pool de forma determinista no se puede (depende de los núcleos y de cuándo
/// inyecta hilos), así que se comprueba la causa, que el trabajo que bloquea no ocupa un hilo del pool, y que con todas
/// las llamadas bloqueadas a la vez el <c>ping</c> se contesta. Sin el arreglo, la función corría en el pool
/// (<c>Task.Run</c>) y la primera prueba falla.
/// </remarks>
public sealed class BlockingWorkTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task Las_funciones_que_bloquean_su_hilo_no_ocupan_el_pool_y_el_ping_se_contesta()
    {
        const int Calls = 4;
        using ManualResetEventSlim release = new();
        ConcurrentQueue<bool> onPool = new();
        using CountdownEvent started = new(Calls);
        ModuleHost host = new(new ModuleInfo("prueba-bloqueo", "1.0.0", "Prueba bloqueo", [new FunctionInfo("bloquear", "Bloquear")]))
        {
            MaxConcurrentCalls = Calls,
        };
        // Bloquea su hilo sin ningún «await», como una inferencia de ONNX Runtime o la carga de un modelo de Whisper.
        host.Register("bloquear", (_, ct) =>
        {
            onPool.Enqueue(Thread.CurrentThread.IsThreadPoolThread);
            started.Signal();
            Assert.True(release.Wait(Guard, ct), "Nadie soltó la llamada.");
            return ValueTask.FromResult(new ModuleOutput());
        });

        (ModuleClient client, Task<bool> run) = await Support.ConnectInProcessAsync(host);
        await using (client)
        {
            Task<ModuleResult>[] calls = [.. Enumerable.Range(0, Calls).Select(_ => client.InvokeAsync("bloquear"))];
            try
            {
                Assert.True(await Task.Run(() => started.Wait(Guard)), "Las llamadas no empezaron.");
                Assert.DoesNotContain(true, onPool);

                // Con todas las llamadas bloqueadas, el módulo sigue contestando.
                await client.PingAsync().WaitAsync(Guard);
            }
            finally
            {
                release.Set();
            }

            await Task.WhenAll(calls).WaitAsync(Guard);
        }

        Assert.True(await run.WaitAsync(Guard));
    }

    [Fact]
    public async Task RunBlockingAsync_corre_fuera_del_pool_y_devuelve_el_resultado()
    {
        Assert.False(await ModuleHost.RunBlockingAsync(() => Thread.CurrentThread.IsThreadPoolThread));

        bool? pool = null;
        await ModuleHost.RunBlockingAsync(() => pool = Thread.CurrentThread.IsThreadPoolThread);
        Assert.False(pool);
    }

    [Fact]
    public async Task RunBlockingAsync_propaga_el_fallo_y_no_empieza_si_ya_se_cancelo()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => ModuleHost.RunBlockingAsync<int>(() => throw new InvalidOperationException("Fallo a propósito.")));

        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();
        bool ran = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ModuleHost.RunBlockingAsync(() => ran = true, cancelled.Token));
        Assert.False(ran);
    }
}
