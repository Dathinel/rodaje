namespace Rodaje.Playback.Tests;

/// <summary>
/// <see cref="DecodeThread.Stop(TimeSpan, Action)"/>: lo que se libera al cerrar corre cuando el hilo ya no puede usarlo,
/// aunque quien cierra deje de esperar. Deterministas: el plazo es cero y el trabajo solo vuelve cuando la prueba lo suelta.
/// </summary>
public class DecodeThreadReleaseTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);

    [Fact]
    public void A_hung_work_delays_the_release_until_it_returns_and_it_runs_on_the_thread_after_the_queue()
    {
        var thread = new DecodeThread("Rodaje prueba liberar colgado", TimeSpan.Zero);
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var order = new List<string>();
        Thread? releasedOn = null;
        thread.Post(() =>
        {
            started.Set();
            release.Wait(Limit);
            lock (order)
            {
                order.Add("trabajo");
            }
        });
        thread.Post(() =>
        {
            lock (order)
            {
                order.Add("cola");
            }
        });
        Assert.True(started.Wait(Limit));

        try
        {
            Assert.False(thread.Stop(TimeSpan.Zero, () =>
            {
                releasedOn = Thread.CurrentThread;
                lock (order)
                {
                    order.Add("liberar");
                }
            }));

            lock (order)
            {
                Assert.Empty(order);
            }
        }
        finally
        {
            release.Set();
        }

        Assert.True(SpinWait.SpinUntil(() => !thread.IsAlive, Limit));
        Assert.Equal(["trabajo", "cola", "liberar"], order);
        Assert.NotSame(Thread.CurrentThread, releasedOn);
    }

    [Fact]
    public void A_thread_that_already_ended_releases_right_away_on_the_caller()
    {
        var thread = new DecodeThread("Rodaje prueba liberar terminado", Limit);
        Assert.True(thread.Stop(Limit));
        int runs = 0;
        Thread? releasedOn = null;

        Assert.True(thread.Stop(TimeSpan.Zero, () =>
        {
            releasedOn = Thread.CurrentThread;
            runs++;
        }));

        Assert.Equal(1, runs);
        Assert.Same(Thread.CurrentThread, releasedOn);
    }
}
