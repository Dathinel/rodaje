namespace Rodaje.App.Tests.Support;

/// <summary>El soporte de pruebas: un <c>Post</c> que llega después de cerrar el hilo de interfaz no tumba el proceso.</summary>
public sealed class UiThreadTests
{
    [Fact]
    public async Task Una_continuacion_tardia_corre_en_el_pool_sin_tumbar_el_proceso()
    {
        SynchronizationContext? context = null;
        UiThread.Run(() => context = SynchronizationContext.Current);
        Assert.NotNull(context);

        int before = UiThread.LatePosts;
        TaskCompletionSource<bool> ran = new(TaskCreationOptions.RunContinuationsAsynchronously);
        // Lo que hizo caer el host en la CI (PR #47): una continuación de await que llega con el hilo ya cerrado.
        context!.Post(_ => ran.TrySetResult(Thread.CurrentThread.IsThreadPoolThread), null);
        Assert.True(await ran.Task.WaitAsync(TimeSpan.FromSeconds(60)));
        Assert.True(UiThread.LatePosts > before);
    }

    [Fact]
    public async Task Un_await_que_sigue_tras_terminar_la_prueba_tampoco()
    {
        TaskCompletionSource late = new();
        TaskCompletionSource<bool> resumed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        UiThread.Run(() =>
        {
            // Sin esperar: la prueba termina y la continuación queda pendiente en el contexto del hilo.
            _ = Continue();
            return Task.CompletedTask;

            async Task Continue()
            {
                await late.Task;
                resumed.TrySetResult(true);
            }
        });

        late.SetResult();
        Assert.True(await resumed.Task.WaitAsync(TimeSpan.FromSeconds(60)));
    }
}
