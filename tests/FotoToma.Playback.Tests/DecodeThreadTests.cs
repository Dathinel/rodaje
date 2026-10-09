using FotoToma.Core.Timing;
using FotoToma.Playback.Tests.Support;

namespace FotoToma.Playback.Tests;

public class DecodeThreadTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public void Post_EjecutaEnOrdenEnOtroHilo()
    {
        var order = new List<int>();
        int caller = Environment.CurrentManagedThreadId;
        int worker = caller;
        using var done = new ManualResetEventSlim();
        using (var thread = new DecodeThread())
        {
            for (int i = 0; i < 100; i++)
            {
                int n = i;
                thread.Post(() =>
                {
                    order.Add(n);
                    worker = Environment.CurrentManagedThreadId;
                });
            }

            thread.Post(done.Set);
            Assert.True(done.Wait(Patience));
            Assert.Throws<ArgumentNullException>(() => thread.Post(null!));
        }

        Assert.Equal(Enumerable.Range(0, 100), order);
        Assert.NotEqual(caller, worker);
    }

    [Fact]
    public void Dispose_TerminaLoEncoladoEIgnoraLoQueLlegaDespues()
    {
        int runs = 0;
        var thread = new DecodeThread();
        thread.Post(() => Thread.Sleep(20));
        thread.Post(() => Interlocked.Increment(ref runs));
        thread.Dispose();
        thread.Dispose();
        thread.Post(() => Interlocked.Increment(ref runs));
        Assert.Equal(1, Volatile.Read(ref runs));
    }

    [Fact]
    public void Dispose_DesdeElPropioHiloNoSeBloquea()
    {
        var thread = new DecodeThread();
        using var done = new ManualResetEventSlim();
        thread.Post(() =>
        {
            thread.Dispose();
            done.Set();
        });
        Assert.True(done.Wait(Patience));
    }

    [Fact]
    public void Programador_ConHiloReal_EntregaElCuadroExactoDelSeek()
    {
        var timing = new ClipTiming(Time.Zero, Time.Zero, Time.FromSeconds(10), Rational.Fps25);
        var tracker = new PayloadTracker();
        var clock = new ManualClock();
        using var ready = new ManualResetEventSlim();
        long readyIndex = -1;
        using (var thread = new DecodeThread())
        using (var cache = new FrameCache(64))
        using (var scheduler = new PlaybackScheduler(timing, clock, new ThreadedSource(timing.FrameRate, tracker), cache, thread))
        {
            scheduler.ExactFrameReady += (_, e) =>
            {
                Volatile.Write(ref readyIndex, e.FrameIndex);
                ready.Set();
            };

            scheduler.SeekToFrame(123);
            Assert.True(ready.Wait(Patience));
            Assert.Equal(123, Volatile.Read(ref readyIndex));

            FramePresentation shown = scheduler.Tick();
            Assert.True(shown.IsExact);
            Assert.Equal(123, shown.Frame!.Index);
        }

        // Lo que se estaba decodificando al cerrar termina en un hilo del pool y se libera allí
        Assert.True(SpinWait.SpinUntil(() => tracker.Live == 0, Patience));
    }

    private sealed class ThreadedSource(Rational rate, PayloadTracker tracker) : IVideoFrameSource
    {
        public async ValueTask<DecodedFrame?> GetFrameAsync(long frameIndex, CancellationToken cancellationToken)
        {
            await Task.Yield();
            return Frames.Make(frameIndex, rate, tracker);
        }
    }
}
