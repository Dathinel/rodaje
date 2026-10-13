using System.Collections.Concurrent;

namespace Rodaje.Module.Raw.Tests;

/// <summary>
/// El reparto de cuadros de «revelar» sin LibRaw: un cuadro que falla cancela los demás y la llamada no responde hasta que
/// todos terminan (#162). Las esperas con plazo solo cortan la prueba si el código vuelve a fallar; no marcan el ritmo.
/// </summary>
public class FrameBatchTests
{
    /// <summary>
    /// Tope anticuelgue de todas las esperas: no mide nada, solo corta si algo se cuelga. Los cuadros ya no salen del pool
    /// (los revelan hilos propios de <c>FrameBatch</c>), pero las esperas siguen dependiendo de él: el bucle de
    /// <c>RunAsync</c> recoge los cuadros y pide la cancelación en continuaciones del pool, y los plazos de
    /// <c>WaitAsync</c> son temporizadores del pool. En la CI de
    /// Windows, con 14 testhost en 4 núcleos, el pool tardó más de 10 s en atender: con un tope de 10 s, el primer cuadro
    /// fallaba antes de que empezara el segundo y la prueba caía sin que <c>FrameBatch</c> hiciera nada mal. Un minuto es
    /// seis veces ese atasco y cuatro veces el que se reprodujo en Linux (4 hilos del pool ocupados 15 s, #189), y una
    /// cancelación rota cae en un minuto por espera, no en cinco.
    /// </summary>
    private static readonly TimeSpan AntiHang = TimeSpan.FromSeconds(60);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task All_frames_develop_and_progress_counts_in_order(int threads)
    {
        var developed = new ConcurrentBag<int>();
        var progress = new List<int>();
        await FrameBatch.RunAsync(4, threads, (i, _) => developed.Add(i), (done, _) =>
        {
            progress.Add(done);
            return ValueTask.CompletedTask;
        }, CancellationToken.None).WaitAsync(AntiHang);

        Assert.Equal([0, 1, 2, 3], developed.Order());
        Assert.Equal([1, 2, 3, 4], progress);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task A_failing_frame_cancels_the_rest_and_waits_for_them(int threads)
    {
        var original = new InvalidOperationException("Cuadro roto.");
        using var release = new ManualResetEventSlim();
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0, inFlight = 0, notCancelled = 0, progressCalls = 0;

        void Develop(int _, CancellationToken token)
        {
            int call = Interlocked.Increment(ref calls);
            Interlocked.Increment(ref inFlight);
            try
            {
                if (call == 1)
                {
                    // Con dos hilos, el primero falla cuando el segundo ya está revelando.
                    if (threads > 1 && !secondStarted.Task.Wait(AntiHang, CancellationToken.None))
                    {
                        Interlocked.Increment(ref notCancelled);
                    }

                    throw original;
                }

                if (call == 2)
                {
                    secondStarted.TrySetResult();
                }

                // Los demás cuadros siguen hasta que los cancelan; el segundo, además, hasta que la prueba lo suelta.
                if (!token.WaitHandle.WaitOne(AntiHang))
                {
                    Interlocked.Increment(ref notCancelled);
                }

                if (call == 2)
                {
                    cancelSeen.TrySetResult();
                    release.Wait(AntiHang, CancellationToken.None);
                }

                token.ThrowIfCancellationRequested();
            }
            finally
            {
                Interlocked.Decrement(ref inFlight);
            }
        }

        Task run = FrameBatch.RunAsync(4, threads, Develop, (_, _) =>
        {
            Interlocked.Increment(ref progressCalls);
            return ValueTask.CompletedTask;
        }, CancellationToken.None);

        if (threads > 1)
        {
            // El segundo cuadro ve la cancelación y sigue en curso: la llamada no puede haber respondido.
            await cancelSeen.Task.WaitAsync(AntiHang);
            Assert.False(run.IsCompleted);
        }

        release.Set();
        InvalidOperationException thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => run.WaitAsync(AntiHang));

        Assert.Same(original, thrown);
        Assert.Equal(0, Volatile.Read(ref inFlight));
        Assert.Equal(0, Volatile.Read(ref notCancelled));
        Assert.Equal(0, Volatile.Read(ref progressCalls));
        Assert.InRange(Volatile.Read(ref calls), threads, 4);
    }
}
