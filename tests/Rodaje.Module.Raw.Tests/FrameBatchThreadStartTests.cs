using System.Runtime.ExceptionServices;

namespace Rodaje.Module.Raw.Tests;

/// <summary>
/// Los hilos de revelado los arranca el hilo que llama (en «revelar», el hilo propio de la llamada), no el pool:
/// <see cref="Thread.Start()"/> espera a que el hilo nuevo arranque, y en la CI de Windows esa espera llegó a pasar de 10 s.
/// Antes cada cuadro arrancaba su hilo desde un trabajo del pool; con la CPU llena, los hilos del pool esperaban esos
/// arranques, el bucle de ModuleHost no contestaba el «ping» y el editor mataba el módulo («RAW de cine» dejó de responder,
/// CinemaDngImportTests en la CI de Windows de #126 y #246). Sin reloj: la costura envuelve cada arranque y mira en qué
/// hilo corre, así que un <c>Thread.Start</c> llevado al pool (aunque se espere desde el hilo que llama) cae.
/// </summary>
public class FrameBatchThreadStartTests
{
    /// <summary>
    /// Tope anticuelgue de las esperas: no mide nada ni marca el ritmo; solo corta la prueba si la cancelación deja de
    /// llegar a los hilos de revelado y la espera no acabaría nunca.
    /// </summary>
    private static readonly TimeSpan AntiHang = TimeSpan.FromSeconds(60);

    [Theory]
    [InlineData(8, 4)]
    [InlineData(3, 8)]
    [InlineData(1, 1)]
    public async Task Every_developing_thread_is_started_by_the_caller_before_the_first_wait(int frames, int threads)
    {
        int starts = 0;
        bool elsewhere = false;
        int startsWhenReturned = -1;
        using var release = new ManualResetEventSlim();
        Thread? caller = null;

        // Los cuadros esperan a que la prueba los suelte: ninguno termina mientras se cuentan los arranques.
        Task run = StartOnOwnThread(
            start => () =>
            {
                // Aquí corre el Thread.Start de verdad: tiene que ser el hilo de la llamada, no uno del pool.
                starts++;
                elsewhere |= Thread.CurrentThread != caller || Thread.CurrentThread.IsThreadPoolThread;
                start();
            },
            () =>
            {
                caller = Thread.CurrentThread;
                Task task = FrameBatch.RunAsync(frames, threads, (_, _) => release.Wait(CancellationToken.None),
                    (_, _) => ValueTask.CompletedTask, CancellationToken.None);
                startsWhenReturned = starts;
                return task;
            });

        Assert.Equal(Math.Min(frames, threads), startsWhenReturned);
        Assert.False(elsewhere);
        Assert.False(run.IsCompleted);

        release.Set();
        await run.WaitAsync(AntiHang);
        Assert.Equal(Math.Min(frames, threads), starts);
    }

    /// <summary>
    /// Si falla el arranque del hilo número <paramref name="failAt"/>, no se arranca ninguno más; los que ya arrancaron se
    /// cancelan y la llamada no termina hasta que salen de su cuadro, y entonces relanza el fallo del arranque. Con el
    /// primero fallando no hay nada que esperar y el fallo sale tal cual.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task A_failed_thread_start_cancels_and_waits_for_the_threads_already_started(int failAt)
    {
        const int frames = 5;
        var boom = new InvalidOperationException("No se pudo arrancar el hilo (prueba).");
        int attempts = 0;
        int entered = 0;
        int exited = 0;
        int progressCalls = 0;
        using var developing = new SemaphoreSlim(0);
        using var cancelSeen = new CountdownEvent(failAt - 1);
        using var release = new ManualResetEventSlim();

        Task run = StartOnOwnThread(
            start => () =>
            {
                if (++attempts == failAt)
                {
                    throw boom;
                }

                start();
                // Hasta que el hilo recién arrancado no tiene su cuadro no se arranca el siguiente: así cada hilo que
                // arrancó está revelando cuando falla el arranque, sin depender del reparto del sistema.
                Assert.True(developing.Wait(AntiHang));
            },
            () => FrameBatch.RunAsync(frames, 8, (_, ct) =>
            {
                Interlocked.Increment(ref entered);
                developing.Release();
                ct.WaitHandle.WaitOne();
                cancelSeen.Signal();
                release.Wait(CancellationToken.None);
                Interlocked.Increment(ref exited);
                ct.ThrowIfCancellationRequested();
            }, (_, _) =>
            {
                Interlocked.Increment(ref progressCalls);
                return ValueTask.CompletedTask;
            }, CancellationToken.None));

        Assert.Equal(failAt, attempts);
        if (failAt > 1)
        {
            // Los hilos arrancados ven la cancelación, pero siguen en su cuadro: la llamada aún no puede haber respondido.
            Assert.True(cancelSeen.Wait(AntiHang));
            Assert.False(run.IsCompleted);
            release.Set();
        }

        InvalidOperationException thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => run.WaitAsync(AntiHang));
        Assert.Same(boom, thrown);
        Assert.Equal(failAt - 1, Volatile.Read(ref entered));
        Assert.Equal(failAt - 1, Volatile.Read(ref exited));
        Assert.Equal(0, Volatile.Read(ref progressCalls));
    }

    /// <summary>
    /// La cancelación de quien llama llega a los hilos que revelan; los cuadros que faltan se dan por cancelados sin
    /// revelarlos, y la llamada no termina hasta que los hilos salen de su cuadro.
    /// </summary>
    [Fact]
    public async Task External_cancellation_stops_the_threads_and_waits_for_them()
    {
        const int frames = 6;
        const int threads = 2;
        int entered = 0;
        int exited = 0;
        int progressCalls = 0;
        using var developing = new SemaphoreSlim(0);
        using var cancelSeen = new CountdownEvent(threads);
        using var release = new ManualResetEventSlim();
        using var cts = new CancellationTokenSource();

        Task run = StartOnOwnThread(
            start => () =>
            {
                start();
                Assert.True(developing.Wait(AntiHang));
            },
            () => FrameBatch.RunAsync(frames, threads, (_, ct) =>
            {
                Interlocked.Increment(ref entered);
                developing.Release();
                ct.WaitHandle.WaitOne();
                cancelSeen.Signal();
                release.Wait(CancellationToken.None);
                Interlocked.Increment(ref exited);
                ct.ThrowIfCancellationRequested();
            }, (_, _) =>
            {
                Interlocked.Increment(ref progressCalls);
                return ValueTask.CompletedTask;
            }, cts.Token));

        Assert.False(run.IsCompleted);
        await cts.CancelAsync();
        Assert.True(cancelSeen.Wait(AntiHang));
        Assert.False(run.IsCompleted);

        release.Set();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(AntiHang));
        Assert.Equal(threads, Volatile.Read(ref entered));
        Assert.Equal(threads, Volatile.Read(ref exited));
        Assert.Equal(0, Volatile.Read(ref progressCalls));
    }

    /// <summary>
    /// Llama a <paramref name="body"/> en un hilo propio, como el de una llamada de ModuleHost, con la costura puesta solo
    /// para ese hilo, y devuelve la tarea de <c>RunAsync</c> cuando ese hilo ya volvió de la llamada.
    /// </summary>
    private static Task StartOnOwnThread(Func<Action, Action> wrap, Func<Task> body)
    {
        Task? run = null;
        ExceptionDispatchInfo? failure = null;
        var caller = new Thread(() =>
        {
            try
            {
                FrameBatch.WrapWorkerStartForTests = wrap;
                run = body();
            }
#pragma warning disable CA1031 // Se relanza en el hilo de la prueba.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                FrameBatch.WrapWorkerStartForTests = null;
            }
        })
        { IsBackground = true };
        caller.Start();
        caller.Join();
        failure?.Throw();
        return run!;
    }
}
