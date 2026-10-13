using Rodaje.App.Views.Player;

namespace Rodaje.App.Tests.Player;

/// <summary>
/// El fallo de los shaders de los monitores se informa una vez: ni una por cada medición pedida mientras compilaban ni
/// ninguna si ya habían fallado antes de la primera. Sin relojes: se espera a la continuación que devuelve Watch.
/// </summary>
public class ScopeReadinessTests
{
    [Fact]
    public async Task LaCompilacionQueFallaDespues_SeInformaUnaVez()
    {
        var compile = new TaskCompletionSource<byte[][]>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task ready = Ready(compile.Task);
        var reports = new List<Exception>();
        int wakes = 0;

        // Se pregunta varias veces mientras compila (como cada medición que no se pudo encolar): una sola continuación.
        Task watch = ScopeReadiness.Watch(ready, reports.Add, () => Interlocked.Increment(ref wakes));
        for (int i = 0; i < 3; i++)
        {
            Assert.Same(watch, ScopeReadiness.Watch(ready, reports.Add, () => Interlocked.Increment(ref wakes)));
        }

        Assert.False(watch.IsCompleted);

        var error = new InvalidOperationException("el shader no compila");
        compile.SetException(error);
        await watch;
        Assert.Same(watch, ScopeReadiness.Watch(ready, reports.Add, () => Interlocked.Increment(ref wakes)));

        Assert.Same(error, Assert.Single(reports));
        Assert.Equal(0, wakes);
    }

    [Fact]
    public async Task LaCompilacionQueYaFallo_SeInformaIgual()
    {
        var error = new InvalidOperationException("el shader no compila");
        Task ready = Ready(Task.FromException<byte[][]>(error));
        try
        {
            await ready;
        }
        catch (AggregateException)
        {
            // Ready ya terminó con el fallo antes de la primera medición.
        }

        Assert.True(ready.IsFaulted);
        var reports = new List<Exception>();
        int wakes = 0;

        await ScopeReadiness.Watch(ready, reports.Add, () => Interlocked.Increment(ref wakes));

        Assert.Same(error, Assert.Single(reports));
        Assert.Equal(0, wakes);
    }

    [Fact]
    public async Task LaCompilacionQueTerminaBien_DespiertaUnaVezYNoInforma()
    {
        var compile = new TaskCompletionSource<byte[][]>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task ready = Ready(compile.Task);
        var reports = new List<Exception>();
        int wakes = 0;

        Task watch = ScopeReadiness.Watch(ready, reports.Add, () => Interlocked.Increment(ref wakes));
        compile.SetResult([]);
        await watch;

        Assert.Empty(reports);
        Assert.Equal(1, wakes);
    }

    // Como ScopeRenderer.Ready: una continuación que lee el resultado de la compilación (el fallo, envuelto dos veces).
    private static Task Ready(Task<byte[][]> compile) =>
        compile.ContinueWith(t => { _ = t.Result.Length; }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
}
