namespace Rodaje.Modules.Protocol.Tests.Fuzz;

/// <summary>
/// Tarea 300: el hilo de arranque de <see cref="ModuleHost.RunBlockingAsync{T}(Func{T}, CancellationToken)"/> (tarea 289)
/// solo atrapaba la cancelación en la cola y el fallo del planificador. Cualquier otra excepción al arrancar un trabajo lo
/// mataba, y como hay uno por proceso, desde entonces ningún trabajo pedido desde el pool (oleadas de Visión, síntesis de
/// Voz) volvía a arrancar: quedaban colgados para siempre. Y fuera del pool, si el token se cancelaba entre la comprobación
/// y el arranque, <c>RunBlockingAsync</c> lanzaba al llamador en vez de devolver la tarea cancelada, como hacía
/// <c>StartNew</c>. La costura <see cref="ModuleHost.BeforeBlockingThreadStartsForTests"/> corre justo antes del arranque y
/// hace de excepción imprevista o de cancelación a destiempo, sin relojes.
/// </summary>
[Collection(FuzzGroup.Name)]
public sealed class BlockingStarterFailureTests
{
    [Fact]
    public void An_unexpected_start_failure_fails_only_that_work_and_later_work_from_the_pool_still_starts() => FailOneStart();

    [Fact]
    public void A_cancellation_just_before_starting_off_the_pool_returns_a_canceled_task() => CancelBeforeStartOffPool();

    /// <summary>Esperas bloqueantes con el plazo de siempre, como en <see cref="BlockingStartPoolTests"/>.</summary>
    private static void FailOneStart()
    {
        InvalidOperationException boom = new("Arranque imprevisto que falla (prueba).");
        Task<int> failed = FromPool(() => throw boom, () => 1);
        Task<int> next = FromPool(null, () => 2);

        Assert.True(((Task)failed).ContinueWith(_ => { }, TaskScheduler.Default).Wait(FuzzSupport.CaseCap),
            "El trabajo cuyo arranque falló quedó sin terminar: el hilo de arranque murió con la excepción.");
        Assert.True(failed.IsFaulted, $"El trabajo cuyo arranque falló terminó en {failed.Status}, no con el fallo.");
        Assert.Same(boom, failed.Exception!.InnerException);

        Assert.True(next.Wait(FuzzSupport.CaseCap),
            "Tras un arranque fallido, un trabajo pedido desde el pool no arrancó: el hilo de arranque ya no existe.");
        Assert.Equal(2, next.Result);
    }

    private static void CancelBeforeStartOffPool()
    {
        using CancellationTokenSource cts = new();
        Task? work = null;
        Exception? thrown = null;
        bool onPool = true;
        Thread caller = new(() =>
        {
            onPool = Thread.CurrentThread.IsThreadPoolThread;
            ModuleHost.BeforeBlockingThreadStartsForTests = cts.Cancel;
            try
            {
                work = ModuleHost.RunBlockingAsync(() => { }, cts.Token);
            }
            catch (Exception e)
            {
                thrown = e;
            }
            finally
            {
                ModuleHost.BeforeBlockingThreadStartsForTests = null;
            }
        })
        { IsBackground = true };
        caller.Start();
        Assert.True(caller.Join(FuzzSupport.CaseCap), "La llamada a RunBlockingAsync fuera del pool no volvió.");

        Assert.False(onPool);
        Assert.Null(thrown);
        Assert.NotNull(work);
        Assert.True(work.IsCanceled, $"La tarea quedó en {work.Status}, no cancelada.");
    }

    /// <summary>Llama a <c>RunBlockingAsync</c> desde un hilo del pool con la costura dada y devuelve la tarea.</summary>
    private static Task<int> FromPool(Action? beforeStart, Func<int> body)
    {
        TaskCompletionSource<Task<int>> called = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ThreadPool.UnsafeQueueUserWorkItem(_ =>
        {
            ModuleHost.BeforeBlockingThreadStartsForTests = beforeStart;
            try
            {
                if (!Thread.CurrentThread.IsThreadPoolThread) throw new InvalidOperationException("Se esperaba un hilo del pool.");
                called.SetResult(ModuleHost.RunBlockingAsync(body));
            }
            catch (Exception e)
            {
                called.SetException(e);
            }
            finally
            {
                ModuleHost.BeforeBlockingThreadStartsForTests = null;
            }
        }, null);
        Assert.True(called.Task.Wait(FuzzSupport.CaseCap), "La llamada a RunBlockingAsync desde el pool no volvió.");
        return called.Task.Result;
    }
}
