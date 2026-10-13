using System.Diagnostics;

namespace Rodaje.Playback.Tests;

/// <summary>
/// Cierre de <see cref="DecodeThread"/> con un trabajo colgado y con dos que lo cierran a la vez. Deterministas: cada espera
/// depende de un hecho que provoca la prueba; los topes solo cortan la espera en rojo.
/// </summary>
public class DecodeThreadStopTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Antes <c>Dispose</c> esperaba al hilo sin tope: un trabajo colgado colgaba a quien cerraba. Ahora vuelve tras su plazo
    /// (cero aquí), lo registra con el nombre del hilo, y lo encolado antes de cerrar corre igual cuando el trabajo vuelve.
    /// </summary>
    [Fact]
    public void Dispose_with_a_hung_work_returns_after_its_timeout_logs_it_and_still_runs_the_queue()
    {
        string name = "Rodaje prueba colgado " + Guid.NewGuid().ToString("N");
        var thread = new DecodeThread(name, TimeSpan.Zero);
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int runs = 0;
        thread.Post(() =>
        {
            started.Set();
            release.Wait(Limit);
        });
        thread.Post(() => Interlocked.Increment(ref runs));
        Assert.True(started.Wait(Limit));

        using var trace = new NameTrace(name);
        try
        {
            var dispose = new Thread(thread.Dispose) { IsBackground = true };
            dispose.Start();
            Assert.True(dispose.Join(Limit), "Dispose esperó al trabajo colgado sin tope.");
            Assert.True(thread.IsAlive);
            Assert.Contains(trace.Lines, l => l.Contains("no terminó", StringComparison.Ordinal));
            thread.Post(() => Interlocked.Increment(ref runs));
        }
        finally
        {
            release.Set();
        }

        Assert.True(SpinWait.SpinUntil(() => !thread.IsAlive, Limit));
        Assert.Equal(1, Volatile.Read(ref runs));
    }

    /// <summary>
    /// La segunda llamada a <c>Dispose</c> también espera al hilo. Antes volvía de inmediato si otro (aquí, el propio hilo)
    /// ya lo había empezado a cerrar, con el trabajo todavía en curso. El trabajo termina solo cuando ve al hilo de la
    /// prueba bloqueado dentro de <c>Dispose</c>: si <c>Dispose</c> no esperara, volvería con el trabajo sin terminar.
    /// </summary>
    [Fact]
    public void A_second_Dispose_also_waits_for_the_work_in_progress()
    {
        var thread = new DecodeThread("Rodaje prueba doble cierre", Limit);
        using var disposedInside = new ManualResetEventSlim();
        Thread caller = Thread.CurrentThread;
        int calling = 0;
        int finished = 0;
        thread.Post(() =>
        {
            thread.Dispose();
            disposedInside.Set();
            SpinWait.SpinUntil(() => Volatile.Read(ref calling) == 1, Limit);
            SpinWait.SpinUntil(() => (caller.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0, Limit);
            Volatile.Write(ref finished, 1);
        });

        Assert.True(disposedInside.Wait(Limit));
        Volatile.Write(ref calling, 1);
        thread.Dispose();
        Assert.Equal(1, Volatile.Read(ref finished));
        Assert.False(thread.IsAlive);
    }

    /// <summary>Junta lo que se escribe con <see cref="Trace"/> y menciona <c>filter</c> (las pruebas corren en paralelo).</summary>
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
