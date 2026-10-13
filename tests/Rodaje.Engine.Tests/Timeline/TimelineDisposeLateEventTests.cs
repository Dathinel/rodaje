using System.Diagnostics;
using Rodaje.Core.Timing;
using Rodaje.Engine.Tests.Support;
using Rodaje.Engine.Timeline;
using Rodaje.Media;
using static Rodaje.Engine.Tests.Support.Seconds;

namespace Rodaje.Engine.Tests.Timeline;

/// <summary>
/// Lo que un decodificador entrega después de cerrar la sesión no sale del motor: un cuadro colgado que vuelve con un
/// error tras <c>Dispose</c> llegaba a la interfaz como <c>DecodeFailed</c>, con la vista cerrada u otro proyecto abierto.
/// </summary>
/// <remarks>
/// Determinista: el cuadro colgado solo termina cuando <c>Dispose</c> cancela su petición, y la prueba espera a que la
/// continuación del programador lo procese (el aviso o la línea del registro), no a un reloj; el tope solo corta la espera
/// en rojo.
/// </remarks>
public sealed class TimelineDisposeLateEventTests
{
    /// <summary>
    /// Tope anticuelgue: no mide nada, solo corta si algo se cuelga. La continuación del cuadro colgado corre en el pool de
    /// hilos (la tarea del doble termina con <c>RunContinuationsAsynchronously</c>), y en la CI de Windows, con el pool
    /// saturado por otros testhost, 20 s no alcanzaban para que llegara su turno.
    /// </summary>
    private static readonly TimeSpan AntiHang = TimeSpan.FromMinutes(5);

    [Fact]
    public void A_frame_that_fails_after_Dispose_raises_no_DecodeFailed_and_is_traced()
    {
        string hung = TimelineBuilder.PathOf("m0");
        var backend = new FakeTimelineBackend
        {
            ManualDecoding = true,
            HangFrame = (path, _) => path == hung,
            HangFailure = () => new MediaException("Cuadro de prueba perdido al cerrar."),
        };
        var b = new TimelineBuilder(2, 0);
        for (int i = 0; i < 2; i++)
        {
            b.AddMedia("m" + i, Rational.Fps30, 900, audio: false, backend: backend);
            b.AddVideo(i, "m" + i, Time.Zero, Time.Zero, S(10));
        }

        TimelineSession session = TimelineSessionTests.Open(b, backend, new TimelineSessionOptions { EnableAudio = false });
        for (int i = 0; i < 10; i++)
        {
            session.Tick().Dispose();
            backend.RunDecoders();
        }

        Assert.Equal(1, backend.HungFrames);

        using var raised = new ManualResetEventSlim();
        int failures = 0;
        session.DecodeFailed += (_, _) =>
        {
            Interlocked.Increment(ref failures);
            raised.Set();
        };
        using var trace = new LateTrace(hung, raised);

        session.Dispose();

        // La cancelación de Dispose hace fallar el cuadro y la continuación corre en el pool: termina con el aviso o con
        // la línea del registro, cuando el pool le dé turno.
        Assert.True(raised.Wait(AntiHang), "La continuación del cuadro colgado no terminó.");
        Assert.Equal(0, Volatile.Read(ref failures));
        Assert.Contains(trace.Lines, l => l.Contains("no se avisa", StringComparison.Ordinal)
            && l.Contains("Cuadro de prueba perdido al cerrar.", StringComparison.Ordinal));
        Assert.Equal(0, backend.HungFrames);
        Assert.Empty(backend.OpenVideos);
    }

    /// <summary>Junta lo que se escribe con <see cref="Trace"/> sobre <c>filter</c> y avisa en la primera línea.</summary>
    private sealed class LateTrace : TraceListener
    {
        private readonly string _filter;
        private readonly ManualResetEventSlim _seen;
        private readonly List<string> _lines = [];

        public LateTrace(string filter, ManualResetEventSlim seen)
        {
            _filter = filter;
            _seen = seen;
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
            if (message is not null && message.Contains(_filter, StringComparison.Ordinal)
                && message.Contains("no se avisa", StringComparison.Ordinal))
            {
                lock (_lines)
                {
                    _lines.Add(message);
                }

                _seen.Set();
            }
        }

        protected override void Dispose(bool disposing)
        {
            Trace.Listeners.Remove(this);
            base.Dispose(disposing);
        }
    }
}
