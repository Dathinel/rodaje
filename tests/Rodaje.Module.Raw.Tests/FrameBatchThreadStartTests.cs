using System.Runtime.ExceptionServices;

namespace Rodaje.Module.Raw.Tests;

/// <summary>
/// Los hilos de revelado los arranca el hilo que llama (en «revelar», el hilo propio de la llamada), no el pool:
/// <see cref="Thread.Start()"/> espera a que el hilo nuevo arranque, y en la CI de Windows esa espera llegó a pasar de 10 s.
/// Antes cada cuadro arrancaba su hilo desde un trabajo del pool; con la CPU llena, los hilos del pool esperaban esos
/// arranques, el bucle de ModuleHost no contestaba el «ping» y el editor mataba el módulo («RAW de cine» dejó de responder,
/// CinemaDngImportTests en la CI de Windows de #126 y #246). Sin reloj: la costura cuenta los arranques en el hilo que llama.
/// </summary>
public class FrameBatchThreadStartTests
{
    [Theory]
    [InlineData(8, 4)]
    [InlineData(3, 8)]
    [InlineData(1, 1)]
    public async Task Every_developing_thread_is_started_by_the_caller_before_the_first_wait(int frames, int threads)
    {
        int starts = 0;
        bool fromPool = false;
        int startsWhenReturned = -1;
        Task? run = null;
        ExceptionDispatchInfo? failure = null;
        using var release = new ManualResetEventSlim();

        // Un hilo propio, como el de una llamada de ModuleHost. La costura es por hilo: un arranque hecho desde el pool no
        // la llamaría y la cuenta no saldría.
        var caller = new Thread(() =>
        {
            try
            {
                FrameBatch.BeforeWorkerStartsForTests = () =>
                {
                    starts++;
                    fromPool |= Thread.CurrentThread.IsThreadPoolThread;
                };
                // Los cuadros esperan a que la prueba los suelte: ninguno termina mientras se cuentan los arranques.
                run = FrameBatch.RunAsync(frames, threads, (_, _) => release.Wait(CancellationToken.None), (_, _) => ValueTask.CompletedTask, CancellationToken.None);
                startsWhenReturned = starts;
            }
#pragma warning disable CA1031 // Se relanza en el hilo de la prueba.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                FrameBatch.BeforeWorkerStartsForTests = null;
            }
        })
        { IsBackground = true };
        caller.Start();
        caller.Join();
        failure?.Throw();

        Assert.Equal(Math.Min(frames, threads), startsWhenReturned);
        Assert.False(fromPool);
        Assert.False(run!.IsCompleted);

        release.Set();
        await run;
        Assert.Equal(Math.Min(frames, threads), starts);
    }
}
