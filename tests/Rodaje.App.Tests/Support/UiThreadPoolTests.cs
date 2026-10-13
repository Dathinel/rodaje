using System.Diagnostics;
using Xunit.Abstractions;

namespace Rodaje.App.Tests.Support;

/// <summary>
/// <see cref="UiThread.Run(Func{Task})"/> bloquea el hilo del pool en que xUnit corre la prueba mientras dura. Con
/// <c>Thread.Join</c> el pool no lo sabía y, con muchas a la vez, el resto del trabajo esperaba a la inyección lenta de
/// hilos; esperando una Task, el pool pone otro hilo en lugar de cada uno bloqueado.
/// </summary>
public sealed class UiThreadPoolTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Categoria", "TiempoReal")]
    public async Task Muchas_pruebas_de_interfaz_a_la_vez_no_dejan_el_pool_sin_hilos()
    {
        // El doble del mínimo de este proyecto (32): sin aviso al pool, los que pasan del mínimo esperaban a la inyección
        // lenta. En Linux con 4 núcleos: 34 s con Thread.Join, 3,4 s esperando una Task. Es TiempoReal porque mide contra
        // el reloj y porque deja el pool lleno unos segundos, lo que estorbaría a las demás pruebas en paralelo.
        const int Tests = 64;
        int started = 0;
        TaskCompletionSource allStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var watch = Stopwatch.StartNew();
        Task[] runs = [.. Enumerable.Range(0, Tests).Select(_ => Task.Run(() => UiThread.Run(async () =>
        {
            if (Interlocked.Increment(ref started) == Tests)
            {
                allStarted.TrySetResult();
            }

            await release.Task;
        })))];
        try
        {
            await allStarted.Task.WaitAsync(TimeSpan.FromSeconds(60));
        }
        finally
        {
            release.TrySetResult();
        }

        TimeSpan elapsed = watch.Elapsed;
        await Task.WhenAll(runs).WaitAsync(TimeSpan.FromSeconds(60));
        output.WriteLine($"{Tests} pruebas de interfaz a la vez empezaron en {elapsed.TotalMilliseconds:0} ms. {UiThread.PoolState()}");
        Assert.True(elapsed < TimeSpan.FromSeconds(15), $"Tardaron {elapsed.TotalSeconds:0.0} s en empezar. {UiThread.PoolState()}");
    }
}
