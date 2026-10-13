using System.Diagnostics;

namespace Rodaje.Playback.Tests;

/// <summary>
/// <see cref="DecodeThread"/> cuando un trabajo o una liberación lanza. Antes la excepción salía del hilo sin atrapar: el
/// proceso caía (aquí, el de las pruebas) y la liberación encargada con <see cref="DecodeThread.Stop(TimeSpan, Action)"/>
/// no corría. Deterministas: el hilo solo avanza cuando la prueba suelta su señal y el fin se espera con
/// <see cref="DecodeThread.Stop(TimeSpan)"/> (un <c>Join</c>); los topes solo cortan la espera en rojo.
/// </summary>
public class DecodeThreadFaultTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);

    [Fact]
    public void A_work_that_throws_is_logged_and_the_queue_and_the_release_still_run_on_the_thread()
    {
        string name = "Rodaje prueba trabajo lanza " + Guid.NewGuid().ToString("N");
        var thread = new DecodeThread(name, Limit);
        using var gate = new ManualResetEventSlim();
        using var trace = new NameTrace(name);
        var order = new List<string>();
        Thread? releasedOn = null;
        thread.Post(() =>
        {
            gate.Wait(Limit);
            throw new InvalidOperationException("trabajo roto");
        });
        thread.Post(() =>
        {
            lock (order)
            {
                order.Add("cola");
            }
        });

        try
        {
            // Encargada mientras el hilo sigue detenido en el primer trabajo: tiene que correr al salir, en el hilo.
            Assert.False(thread.Stop(TimeSpan.Zero, () =>
            {
                releasedOn = Thread.CurrentThread;
                lock (order)
                {
                    order.Add("liberar");
                }
            }));
        }
        finally
        {
            gate.Set();
        }

        Assert.True(thread.Stop(Limit), "El hilo no terminó tras el trabajo que lanzó.");
        lock (order)
        {
            Assert.Equal(["cola", "liberar"], order);
        }

        Assert.NotNull(releasedOn);
        Assert.NotSame(Thread.CurrentThread, releasedOn);
        Assert.Contains(trace.Lines, l => l.Contains("trabajo roto", StringComparison.Ordinal));
    }

    [Fact]
    public void A_release_that_throws_on_the_thread_is_logged_and_the_next_release_still_runs()
    {
        string name = "Rodaje prueba liberar lanza " + Guid.NewGuid().ToString("N");
        var thread = new DecodeThread(name, Limit);
        using var gate = new ManualResetEventSlim();
        using var started = new ManualResetEventSlim();
        using var trace = new NameTrace(name);
        int released = 0;
        thread.Post(() =>
        {
            started.Set();
            gate.Wait(Limit);
        });
        Assert.True(started.Wait(Limit));

        try
        {
            Assert.False(thread.Stop(TimeSpan.Zero, () => throw new InvalidOperationException("liberar roto")));
            Assert.False(thread.Stop(TimeSpan.Zero, () => Interlocked.Increment(ref released)));
        }
        finally
        {
            gate.Set();
        }

        Assert.True(thread.Stop(Limit), "El hilo no terminó tras la liberación que lanzó.");
        Assert.Equal(1, Volatile.Read(ref released));
        Assert.False(thread.IsAlive);
        Assert.Contains(trace.Lines, l => l.Contains("liberar roto", StringComparison.Ordinal));
    }

    [Fact]
    public void After_a_work_throws_the_thread_keeps_taking_new_work()
    {
        var thread = new DecodeThread("Rodaje prueba sigue tras lanzar", Limit);
        using var done = new ManualResetEventSlim();
        thread.Post(() => throw new InvalidOperationException("trabajo roto"));
        thread.Post(done.Set);

        Assert.True(done.Wait(Limit), "El hilo dejó de atender la cola tras un trabajo que lanzó.");
        Assert.True(thread.IsAlive);
        Assert.True(thread.Stop(Limit));
    }

    /// <summary>Junta las líneas del registro que nombran el hilo de la prueba (las pruebas corren en paralelo).</summary>
    private sealed class NameTrace : TraceListener
    {
        private readonly string _filter;
        private readonly List<string> _lines = [];

        public NameTrace(string filter)
        {
            _filter = filter;
            Trace.Listeners.Add(this);
        }

        public IReadOnlyList<string> Lines
        {
            get
            {
                lock (_lines)
                {
                    return [.. _lines];
                }
            }
        }

        public override void Write(string? message) => WriteLine(message);

        public override void WriteLine(string? message)
        {
            if (message is not null && message.Contains(_filter, StringComparison.Ordinal))
            {
                lock (_lines)
                {
                    _lines.Add(message);
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            Trace.Listeners.Remove(this);
            base.Dispose(disposing);
        }
    }
}
