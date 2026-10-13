using System.Diagnostics;
using Rodaje.Core.Timing;
using Rodaje.Engine.Tests.Support;
using Rodaje.Engine.Timeline;
using Rodaje.Media.Video;
using Rodaje.Playback;
using static Rodaje.Engine.Tests.Support.Seconds;

namespace Rodaje.Engine.Tests.Timeline;

/// <summary>
/// El plazo de cierre del programador de la línea de tiempo: <see cref="TimelineScheduler.Dispose"/> espera, en total,
/// <see cref="TimelineScheduler.CloseTimeout"/> a los hilos de los decodificadores y al de servicio, y el cierre de un
/// decodificador ocioso no deja esperando al hilo de servicio. Complementa <see cref="TimelineDisposeHangTests"/>, cuyo
/// falso da a cada hilo un plazo propio de cero y por eso no distinguía entre esperar el plazo común o el de cada hilo.
/// </summary>
/// <remarks>
/// Sin cotas de reloj en la CI: aquí cada hilo tiene un plazo propio de una hora y el común es cero, así que lo que no
/// respete el plazo común se queda esperando y la prueba cae por su tope (de 20 s, que solo corta la espera en rojo). La
/// cota de reloj fina queda en la gemela <c>TiempoReal</c>. Los medios tienen nombres propios para que el registro de
/// otras pruebas que corren en paralelo no se mezcle con el de estas.
/// </remarks>
public sealed class TimelineCloseDeadlineTests
{
    private static readonly Rational Fps30 = Rational.Fps30;
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    /// <summary>
    /// Tres decodificadores que no terminan de liberarse: el cierre los espera hasta el plazo común y no hasta el de cada
    /// hilo (si a cada hilo se le pasara su propio plazo en vez del común, las demás pruebas del cierre seguirían en
    /// verde). Los tres siguen vivos y quedan en el registro; cuando vuelven, sus hilos terminan de liberarlos.
    /// </summary>
    [Fact]
    public void Dispose_waits_the_common_deadline_and_not_each_threads_own_timeout()
    {
        var backend = new FakeTimelineBackend { RealThreads = true, JoinTimeout = Hour };
        TimelineBuilder b = Build(backend, "plazo-a", 3);
        TimelineScheduler scheduler = Open(b, backend, TimeSpan.Zero);
        WaitOpen(scheduler, backend, 3);

        using var release = new ManualResetEventSlim();
        backend.OnVideoDispose = _ => release.Wait(TimeSpan.FromSeconds(60));
        using var trace = new TraceCapture("plazo-a");
        try
        {
            Stress.Within(0, "Dispose con tres decodificadores colgados", Limit, scheduler.Dispose);

            Assert.Equal(3, backend.OpenVideos.Count());
            for (int i = 0; i < 3; i++)
            {
                string path = TimelineBuilder.PathOf($"plazo-a{i}");
                Assert.Contains(trace.Lines, l => l.Contains(path, StringComparison.Ordinal) && l.Contains("no terminó de cerrarse", StringComparison.Ordinal));
            }
        }
        finally
        {
            release.Set();
        }

        Stress.WaitUntil(0, "liberar los decodificadores colgados", () => !backend.OpenVideos.Any(), Limit);
    }

    /// <summary>
    /// El hilo de servicio entra en el mismo plazo: antes se lo esperaba con su propio plazo después del de los
    /// decodificadores. Aquí lee los keyframes de un clip en reversa y esa lectura no vuelve.
    /// </summary>
    [Fact]
    public void Dispose_gives_the_service_thread_no_timeout_of_its_own()
    {
        var backend = new FakeTimelineBackend { RealThreads = true, JoinTimeout = Hour };
        using var reading = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        backend.OnReadKeyframes = _ =>
        {
            reading.Set();
            release.Wait(TimeSpan.FromSeconds(60));
            return null;
        };
        TimelineBuilder b = Build(backend, "plazo-b", 1);
        SpeedClips.Reversed(b.Sequence.VideoTracks[0].Clips[0], new Rational(1, 1), Time.Zero, S(10));
        TimelineScheduler scheduler = Open(b, backend, TimeSpan.Zero);
        WaitOpen(scheduler, backend, 1);
        WaitReading(scheduler, reading);

        try
        {
            Stress.Within(0, "Dispose con el servicio ocupado", Limit, scheduler.Dispose);
        }
        finally
        {
            release.Set();
        }

        Stress.WaitUntil(0, "liberar el decodificador", () => !backend.OpenVideos.Any(), Limit);
    }

    /// <summary>
    /// Cerrar por falta de uso decodificadores que no terminan de liberarse no detiene al hilo de servicio: antes los
    /// esperaba de a uno, hasta 5 s cada uno, y mientras no atendía keyframes, cuadros ni otros cierres. Aquí, tres colgados
    /// y después otro que se libera normal: se libera mientras los tres siguen colgados, sin que nadie los haya esperado
    /// (esperarlos deja en el registro que no terminaron a tiempo).
    /// </summary>
    [Fact]
    public void Closing_hung_idle_decoders_does_not_block_the_service_thread()
    {
        var backend = new FakeTimelineBackend { RealThreads = true };
        TimelineBuilder b = Build(backend, "plazo-c", 4);
        TimelineScheduler scheduler = Open(b, backend, TimeSpan.Zero, idle: TimeSpan.Zero);
        WaitOpen(scheduler, backend, 4);

        string[] hung = [.. Enumerable.Range(0, 3).Select(i => TimelineBuilder.PathOf($"plazo-c{i}"))];
        string last = TimelineBuilder.PathOf("plazo-c3");
        int entered = 0;
        using var release = new ManualResetEventSlim();
        backend.OnVideoDispose = video =>
        {
            if (hung.Contains(video.Path))
            {
                Interlocked.Increment(ref entered);
                release.Wait(TimeSpan.FromSeconds(60));
            }
        };

        using var trace = new TraceCapture("plazo-c");
        try
        {
            for (int i = 0; i < 3; i++)
            {
                b.Sequence.VideoTracks[i].Clips.Clear();
            }

            scheduler.Reload(b.Plan());
            Stress.WaitUntil(0, "cerrar los tres decodificadores ociosos", () =>
            {
                scheduler.Tick().Dispose();
                return Volatile.Read(ref entered) == 3;
            });

            b.Sequence.VideoTracks[3].Clips.Clear();
            scheduler.Reload(b.Plan());
            Stress.WaitUntil(0, "liberar el cuarto decodificador", () =>
            {
                scheduler.Tick().Dispose();
                return !backend.OpenVideos.Any(v => v.Path == last);
            });

            Assert.Equal(hung.Order(StringComparer.Ordinal), backend.OpenVideos.Select(v => v.Path).Order(StringComparer.Ordinal));
            Assert.DoesNotContain(trace.Lines, l => l.Contains("no terminó de cerrarse", StringComparison.Ordinal));
        }
        finally
        {
            release.Set();
        }

        Stress.WaitUntil(0, "liberar los decodificadores colgados", () => !backend.OpenVideos.Any(), Limit);
        Stress.Within(0, "Dispose", Limit, scheduler.Dispose);
    }

    /// <summary>La cota de reloj: tres decodificadores y el servicio colgados, plazo de 400 ms; cerrar tarda menos del doble.</summary>
    [Fact]
    [Trait("Categoria", "TiempoReal")]
    public void Dispose_with_three_hung_decoders_and_a_hung_service_returns_within_twice_the_deadline()
    {
        TimeSpan deadline = TimeSpan.FromMilliseconds(400);
        var backend = new FakeTimelineBackend { RealThreads = true };
        using var reading = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        backend.OnReadKeyframes = _ =>
        {
            reading.Set();
            release.Wait(TimeSpan.FromSeconds(60));
            return null;
        };
        TimelineBuilder b = Build(backend, "plazo-d", 3);
        SpeedClips.Reversed(b.Sequence.VideoTracks[0].Clips[0], new Rational(1, 1), Time.Zero, S(10));
        TimelineScheduler scheduler = Open(b, backend, deadline);
        WaitOpen(scheduler, backend, 3);
        WaitReading(scheduler, reading);
        backend.OnVideoDispose = _ => release.Wait(TimeSpan.FromSeconds(60));

        var watch = Stopwatch.StartNew();
        try
        {
            Stress.Within(0, "Dispose con todo colgado", Limit, scheduler.Dispose);
            watch.Stop();
        }
        finally
        {
            release.Set();
        }

        Assert.True(watch.Elapsed < deadline * 2, $"Cerrar tardó {watch.Elapsed.TotalMilliseconds:0} ms con un plazo de {deadline.TotalMilliseconds:0} ms.");
        Stress.WaitUntil(0, "liberar los decodificadores colgados", () => !backend.OpenVideos.Any(), Limit);
    }

    /// <summary>Lo que queda del plazo común: nunca negativo y nunca más que el plazo propio del hilo.</summary>
    [Theory]
    [InlineData(1000, 400, 1000, 5.0, 0.6)]
    [InlineData(1000, 1000, 1000, 5.0, 0.0)]
    [InlineData(1000, 1500, 1000, 5.0, 0.0)]
    [InlineData(100_000, 0, 10, 5.0, 5.0)]
    [InlineData(100_000, 0, 10, 0.0, 0.0)]
    public void Remaining_is_the_time_left_capped_by_the_threads_own_timeout(long deadline, long now, long frequency, double cap, double expected) =>
        Assert.Equal(TimeSpan.FromSeconds(expected), TimelineScheduler.Remaining(deadline, now, frequency, TimeSpan.FromSeconds(cap)));

    /// <summary>
    /// Volver a cerrar un hilo que sigue colgado no repite la línea del registro: la sesión y el cierre por falta de uso
    /// pueden pedirlo los dos.
    /// </summary>
    [Fact]
    public void A_hung_decode_thread_is_logged_once_however_many_times_it_is_stopped()
    {
        string name = $"Rodaje decodificación (prueba de registro {Guid.NewGuid():N})";
        var thread = new DecodeThread(name, Hour);
        using var inside = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        thread.Post(() =>
        {
            inside.Set();
            release.Wait(TimeSpan.FromSeconds(60));
        });
        Assert.True(inside.Wait(Limit), "El hilo no empezó el trabajo.");

        using var trace = new TraceCapture(name);
        try
        {
            Assert.False(thread.Stop(TimeSpan.Zero));
            Assert.False(thread.Stop(TimeSpan.Zero));
            thread.RequestStop();
            Assert.False(thread.Stop(TimeSpan.Zero));
        }
        finally
        {
            release.Set();
        }

        Assert.True(thread.Stop(Limit));
        Assert.Single(trace.Lines);
    }

    private static TimelineScheduler Open(TimelineBuilder b, FakeTimelineBackend backend, TimeSpan closeTimeout, TimeSpan? idle = null)
    {
        var options = new TimelineSessionOptions { EnableAudio = false, DecoderIdleTimeout = idle ?? TimeSpan.FromMinutes(10) };
        return new TimelineScheduler(b.Plan(), new SystemClock(), backend, new VideoDecoderOptions(), options, 8L << 30)
        {
            CloseTimeout = closeTimeout,
        };
    }

    private static void WaitReading(TimelineScheduler scheduler, ManualResetEventSlim reading) =>
        Stress.WaitUntil(0, "que el servicio empiece a leer los keyframes", () =>
        {
            // Los keyframes se piden al conocer el archivo que abrió el decodificador, en la siguiente vuelta de la demanda.
            scheduler.Seek(S(5));
            scheduler.Tick().Dispose();
            return reading.IsSet;
        }, Limit);

    private static void WaitOpen(TimelineScheduler scheduler, FakeTimelineBackend backend, int count) =>
        Stress.WaitUntil(0, $"abrir los {count} decodificadores", () =>
        {
            scheduler.Tick().Dispose();
            return backend.OpenVideos.Count() == count;
        }, Limit);

    /// <summary>Una pista de video por medio, de principio a fin: un decodificador por medio.</summary>
    private static TimelineBuilder Build(FakeTimelineBackend backend, string prefix, int media)
    {
        var b = new TimelineBuilder(media, 0);
        for (int i = 0; i < media; i++)
        {
            b.AddMedia(prefix + i, Fps30, 900, audio: false, backend: backend);
            b.AddVideo(i, prefix + i, Time.Zero, Time.Zero, S(10));
        }

        return b;
    }

    /// <summary>Junta lo que se escribe con <see cref="Trace"/> y menciona <c>filter</c> (las pruebas corren en paralelo).</summary>
    private sealed class TraceCapture : TraceListener
    {
        private readonly string _filter;
        private readonly List<string> _lines = [];

        public TraceCapture(string filter)
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
