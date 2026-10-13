using Rodaje.Core.Timing;
using Rodaje.Engine.Tests.Support;
using Rodaje.Engine.Timeline;
using static Rodaje.Engine.Tests.Support.Seconds;

namespace Rodaje.Engine.Tests.Timeline;

/// <summary>
/// Regresión del cierre de la sesión con varios decodificadores (CI de Windows: «Dispose de la sesión no terminó en 20 s»
/// con la semilla 202 de <see cref="TimelineStressTests"/>). El cierre liberaba los decodificadores de a uno en el
/// ejecutor de servicio, detrás de las liberaciones de los cerrados por falta de uso que todavía tuviera en cola: cerrar
/// tardaba la suma de todas y, con cientos de cierres pendientes y liberaciones lentas, más que el tope.
/// </summary>
/// <remarks>
/// Deterministas sin depender del reloj: la liberación de cada decodificador falso no termina hasta que empezaron todas.
/// Liberadas de a una, la primera espera para siempre a las demás (el tope solo corta la espera de la prueba en rojo);
/// liberadas a la vez, pasan enseguida.
/// </remarks>
public sealed class TimelineDisposeTests
{
    private const int Media = 4;
    private static readonly Rational Fps30 = Rational.Fps30;
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);

    [Fact]
    public void Dispose_releases_every_decoder_at_once()
    {
        var backend = new FakeTimelineBackend { RealThreads = true };
        TimelineBuilder b = Build(backend);
        TimelineSession session = TimelineSessionTests.Open(b, backend, new TimelineSessionOptions { EnableAudio = false });
        Stress.WaitUntil(0, "abrir los cuatro decodificadores", () =>
        {
            session.Tick().Dispose();
            return backend.OpenVideos.Count() == Media;
        });

        using var gate = new DisposeGate(backend, Media);
        Stress.Within(0, "Dispose", Limit, session.Dispose);
        Assert.False(gate.TimedOut, "Las liberaciones de los decodificadores no corrieron a la vez.");
        Assert.Empty(backend.OpenVideos);
    }

    /// <summary>
    /// Lo que pasó en la CI: decodificadores cerrados por falta de uso cuya liberación sigue en la cola de servicio (aquí,
    /// la primera trabada) no deben retrasar la de los demás al cerrar la sesión.
    /// </summary>
    [Fact]
    public void Dispose_does_not_wait_behind_the_pending_releases_of_idle_decoders()
    {
        var backend = new FakeTimelineBackend { RealThreads = true };
        TimelineBuilder b = Build(backend);
        TimelineSession session = TimelineSessionTests.Open(b, backend,
            new TimelineSessionOptions { EnableAudio = false, DecoderIdleTimeout = TimeSpan.Zero });
        Stress.WaitUntil(0, "abrir los cuatro decodificadores", () =>
        {
            session.Tick().Dispose();
            return backend.OpenVideos.Count() == Media;
        });

        using var gate = new DisposeGate(backend, Media);

        // Dos medios dejan de usarse: sus decodificadores se cierran y el ejecutor de servicio queda liberando el primero.
        b.Sequence.VideoTracks[0].Clips.Clear();
        b.Sequence.VideoTracks[1].Clips.Clear();
        session.Reload(b.Project);
        Stress.WaitUntil(0, "empezar a liberar un decodificador ocioso", () =>
        {
            session.Tick().Dispose();
            return gate.Arrived >= 1;
        });

        Stress.Within(0, "Dispose con liberaciones pendientes", Limit, session.Dispose);
        Assert.False(gate.TimedOut, "El cierre esperó detrás de las liberaciones pendientes.");
        Assert.Empty(backend.OpenVideos);
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

    /// <summary>Cada liberación de un decodificador falso espera a que hayan empezado <c>expected</c> liberaciones.</summary>
    private sealed class DisposeGate : IDisposable
    {
        private readonly FakeTimelineBackend _backend;
        private readonly int _expected;
        private readonly object _gate = new();
        private int _arrived;
        private bool _open;
        private int _timedOut;

        public DisposeGate(FakeTimelineBackend backend, int expected)
        {
            _backend = backend;
            _expected = expected;
            backend.OnVideoDispose = _ => Arrive();
        }

        public int Arrived => Volatile.Read(ref _arrived);

        public bool TimedOut => Volatile.Read(ref _timedOut) != 0;

        /// <summary>Suelta a quien siga esperando (solo en rojo) y deja de frenar las liberaciones.</summary>
        public void Dispose()
        {
            _backend.OnVideoDispose = null;
            lock (_gate)
            {
                _open = true;
                Monitor.PulseAll(_gate);
            }
        }

        private void Arrive()
        {
            lock (_gate)
            {
                _open |= Interlocked.Increment(ref _arrived) >= _expected;
                Monitor.PulseAll(_gate);
                while (!_open)
                {
                    if (!Monitor.Wait(_gate, Limit))
                    {
                        Interlocked.Exchange(ref _timedOut, 1);
                        break;
                    }
                }
            }
        }
    }
}
