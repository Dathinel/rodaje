using System.Diagnostics;
using Rodaje.Core.Timing;
using Rodaje.Engine.Tests.Support;
using Rodaje.Engine.Timeline;
using static Rodaje.Engine.Tests.Support.Seconds;

namespace Rodaje.Engine.Tests.Timeline;

/// <summary>
/// Cierre de la sesión cuando un decodificador no colabora: un cuadro en vuelo que solo termina si se cancela, una
/// liberación que no vuelve o una que lanza. Complementa <see cref="TimelineDisposeTests"/>.
/// </summary>
/// <remarks>
/// Deterministas: cada espera depende de un hecho (una cancelación, una puerta que abre la prueba), no del reloj; los
/// topes solo cortan la espera de la prueba en rojo.
/// </remarks>
public sealed class TimelineDisposeHangTests
{
    private const int Media = 4;
    private static readonly Rational Fps30 = Rational.Fps30;
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Un decodificador con un cuadro en vuelo no se cierra por falta de uso hasta que ese cuadro termina: así, ningún
    /// decodificador cerrado tiene un <c>GetFrameAsync</c> pendiente, y el cierre de la sesión no puede quedar esperando uno
    /// que nadie cancela. La cancelación de la época de los ya cerrados en <c>Dispose</c> queda como defensa por si eso
    /// cambia. Aquí dos medios dejan de usarse: el que decodifica normal se cierra, el colgado no, y el cierre de la sesión
    /// cancela su cuadro y lo libera.
    /// </summary>
    /// <remarks>
    /// Sin hilos: los decodificadores trabajan solo cuando la prueba corre sus ejecutores, y el cuadro colgado termina solo
    /// si se cancela su petición, así que cada afirmación es sobre un estado fijo.
    /// </remarks>
    [Fact]
    public void A_decoder_with_a_frame_in_flight_is_not_closed_and_Dispose_cancels_it()
    {
        string hung = TimelineBuilder.PathOf("m0");
        var backend = new FakeTimelineBackend { ManualDecoding = true, HangFrame = (path, _) => path == hung };
        TimelineBuilder b = Build(backend);
        TimelineSession session = TimelineSessionTests.Open(b, backend,
            new TimelineSessionOptions { EnableAudio = false, DecoderIdleTimeout = TimeSpan.Zero });
        Drive(session, backend);
        Assert.Equal(Media, backend.OpenVideos.Count());
        Assert.Equal(1, backend.HungFrames);

        b.Sequence.VideoTracks[0].Clips.Clear();
        b.Sequence.VideoTracks[1].Clips.Clear();
        session.Reload(b.Project);
        Drive(session, backend);

        // El de m1 se cerró por falta de uso (el camino se recorrió); el de m0 sigue abierto mientras su cuadro esté en vuelo.
        Assert.Equal(1, session.Stats.DecodersClosed);
        Assert.Contains(backend.OpenVideos, v => v.Path == hung);
        Assert.DoesNotContain(backend.OpenVideos, v => v.Path == TimelineBuilder.PathOf("m1"));
        Assert.Equal(1, backend.HungFrames);

        session.Dispose();
        Assert.Equal(0, backend.HungFrames);
        Assert.Empty(backend.OpenVideos);
    }

    /// <summary>
    /// Un decodificador cuya liberación no vuelve no deja colgado a quien cierra la sesión: se lo espera con plazo, queda
    /// en el registro cuál fue y, cuando vuelve, su hilo termina de liberarlo.
    /// </summary>
    [Fact]
    public void Dispose_does_not_wait_forever_for_a_decoder_that_never_finishes_releasing()
    {
        var backend = new FakeTimelineBackend { RealThreads = true, JoinTimeout = TimeSpan.Zero };
        TimelineBuilder b = Build(backend);
        TimelineSession session = TimelineSessionTests.Open(b, backend, new TimelineSessionOptions { EnableAudio = false });
        Stress.WaitUntil(0, "abrir los cuatro decodificadores", () =>
        {
            session.Tick().Dispose();
            return backend.OpenVideos.Count() == Media;
        });

        string stuck = TimelineBuilder.PathOf("m0");
        using var release = new ManualResetEventSlim();
        backend.OnVideoDispose = video =>
        {
            if (video.Path == stuck)
            {
                release.Wait(Limit);
            }
        };

        using var trace = new TraceCapture(stuck);
        try
        {
            Stress.Within(0, "Dispose con un decodificador que no termina de liberarse", Limit, session.Dispose);

            Assert.Contains(backend.OpenVideos, v => v.Path == stuck);
            Assert.Contains(trace.Lines, l => l.Contains("no terminó de cerrarse", StringComparison.Ordinal));
        }
        finally
        {
            release.Set();
        }

        // Lo que tenía en cola corre cuando vuelve: queda liberado y nada se libera dos veces.
        Stress.WaitUntil(0, "liberar el decodificador colgado", () => !backend.OpenVideos.Any(), Limit);
    }

    /// <summary>
    /// La excepción de un decodificador al liberarse no escapa: en su hilo nadie la atrapa y tiraba el proceso. Queda en
    /// el registro y los demás se liberan igual.
    /// </summary>
    [Fact]
    public void A_decoder_that_throws_while_releasing_is_logged_and_does_not_stop_the_others()
    {
        var backend = new FakeTimelineBackend();
        TimelineBuilder b = Build(backend);
        TimelineSession session = TimelineSessionTests.Open(b, backend, new TimelineSessionOptions { EnableAudio = false });
        session.Tick().Dispose();
        Assert.Equal(Media, backend.OpenVideos.Count());

        string failing = TimelineBuilder.PathOf("m2");
        backend.DisposeFailure = video => video.Path == failing ? new InvalidOperationException("pool de prueba trabado") : null;
        using var trace = new TraceCapture(failing);

        session.Dispose();

        Assert.Empty(backend.OpenVideos);
        Assert.Contains(trace.Lines, l => l.Contains("pool de prueba trabado", StringComparison.Ordinal));
    }

    /// <summary>Unas vueltas de refresco y decodificación: lo que el cuadro colgado deja hacer.</summary>
    private static void Drive(TimelineSession session, FakeTimelineBackend backend)
    {
        for (int i = 0; i < 10; i++)
        {
            session.Tick().Dispose();
            backend.RunDecoders();
        }
    }

    /// <summary>Cuatro pistas de video, cada una con su medio, de principio a fin: un decodificador por medio.</summary>
    private static TimelineBuilder Build(FakeTimelineBackend backend)
    {
        var b = new TimelineBuilder(Media, 0);
        for (int i = 0; i < Media; i++)
        {
            b.AddMedia("m" + i, Fps30, 900, audio: false, backend: backend);
            b.AddVideo(i, "m" + i, Time.Zero, Time.Zero, S(10));
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
