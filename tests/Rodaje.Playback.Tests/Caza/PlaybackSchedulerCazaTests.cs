using System.Reflection;
using Rodaje.Core.Timing;
using Rodaje.Playback.Tests.Support;
using static Rodaje.Playback.Tests.Support.PlaybackHarness;

namespace Rodaje.Playback.Tests.Caza;

/// <summary>Promesas de la sección «PlaybackScheduler» de docs/arquitectura/reproduccion.md.</summary>
public class PlaybackSchedulerCazaTests
{
    // 25 fps, 0,5 s en la línea de tiempo, origen [2 s, 12 s): cuadros 50 a 299
    private static readonly ClipTiming Clip25 =
        new(Time.FromSeconds(0.5), Time.FromSeconds(2), Time.FromSeconds(12), Rational.Fps25);

    private static readonly Time OneFrame = Frames25(1);

    // «Se construye con `(ClipTiming, IAudioClock, IVideoFrameSource, FrameCache, IDecodeExecutor, PlaybackOptions?)`.»
    // y `PlaybackOptions.PrefetchFrames` (8)
    [Fact]
    public void Construccion_SinOpciones_UsaOchoCuadrosAdelantados()
    {
        var clock = new ManualClock(Clip25.Start);
        var source = new SimulatedSource(Rational.Fps25, new PayloadTracker());
        using var cache = new FrameCache(64);
        using var scheduler = new PlaybackScheduler(Clip25, clock, source, cache, new InlineExecutor(), null);

        Assert.Equal([50, 51, 52, 53, 54, 55, 56, 57], source.Requests);
    }

    // «`Tick` (hilo de render) lee `clock.Position`, la lleva al tiempo del origen y calcula el cuadro debido.»
    [Fact]
    public void Tick_LlevaElRelojAlTiempoDelOrigenYCalculaElCuadroDebido()
    {
        using var h = new PlaybackHarness(Clip25);
        h.Clock.Seek(Time.FromSeconds(1.53));

        FramePresentation p = h.SettleAndTick();

        Assert.Equal(Time.FromSeconds(3.03), p.SourceTime);
        Assert.Equal(75, p.TargetFrame);
    }

    // «Muestra el más reciente disponible hasta ese índice (o repite el último), sin bloquear.»
    [Fact]
    public void Tick_MuestraElMasRecienteHastaElDebidoORepiteElUltimoSinBloquear()
    {
        using var h = new PlaybackHarness(Clip25, i => i >= 53 ? Time.FromSeconds(3600) : Time.Zero, prefetch: 6);
        Assert.Equal(50, h.Scheduler.Tick().Frame!.Index);

        h.Clock.Seek(Clip25.TimelineStartOf(54));
        FramePresentation latest = h.Scheduler.Tick();
        Assert.Equal(52, latest.Frame!.Index);
        Assert.False(latest.IsExact);

        h.Clock.Seek(Clip25.TimelineStartOf(55));
        FramePresentation repeated = h.Scheduler.Tick();
        Assert.Same(latest.Frame, repeated.Frame);
        Assert.True(h.Source.IsBusy);
    }

    // «Cuenta aciertos, fallos, cuadros saltados, refrescos tarde y el desfase máximo entre audio y video.»
    [Fact]
    public void Tick_CuentaAciertosFallosSaltadosTardiosYDesfase()
    {
        using var h = new PlaybackHarness(Clip25, i => i == 52 ? Time.FromSeconds(3600) : Time.Zero, prefetch: 4);
        h.Scheduler.Tick();
        h.Scheduler.ResetStats();
        h.Scheduler.Play();

        h.Clock.Advance(Frames25(2));
        FramePresentation late = h.Scheduler.Tick();
        Assert.Equal(51, late.Frame!.Index);

        PlaybackStats stats = h.Scheduler.Stats;
        Assert.Equal(0, stats.CacheHits);
        Assert.Equal(1, stats.CacheMisses);
        Assert.Equal(1, stats.LateRefreshes);
        Assert.Equal(1, stats.FramesShown);
        Assert.Equal(OneFrame, stats.MaxAvOffset);

        h.Source.Settle();
        h.Clock.Advance(Frames25(2));
        Assert.Equal(54, h.Scheduler.Tick().Frame!.Index);

        stats = h.Scheduler.Stats;
        Assert.Equal(1, stats.CacheHits);
        Assert.Equal(2, stats.FramesDropped);
        Assert.Equal(1, stats.LateRefreshes);
    }

    // «Al reproducir, borra de la caché lo que ya pasó para que el LRU no expulse lo adelantado.»
    [Fact]
    public void Tick_AlReproducir_BorraDeLaCacheLoQueYaPaso()
    {
        using var h = new PlaybackHarness(Clip25, prefetch: 4);
        h.Scheduler.Tick();
        h.Scheduler.Play();

        h.Clock.Advance(Frames25(2));
        Assert.Equal(52, h.Scheduler.Tick().Frame!.Index);

        Assert.False(h.Cache.Contains(50));
        Assert.False(h.Cache.Contains(51));
        Assert.True(h.Cache.Contains(52));
        Assert.True(h.Cache.Contains(53));
    }

    // «Ventana de decodificación: `[debido, debido + PrefetchFrames)`, recalculada en cada `Tick` y antes de cada pedido.»
    [Fact]
    public void Ventana_SeRecalculaEnCadaTick()
    {
        using var h = new PlaybackHarness(Clip25, prefetch: 3);
        Assert.Equal([50, 51, 52], h.Source.Requests);

        h.Clock.Seek(Clip25.TimelineStartOf(100));
        h.Scheduler.Tick();

        Assert.Equal([50, 51, 52, 100, 101, 102], h.Source.Requests);
    }

    // «Ventana de decodificación: `[debido, debido + PrefetchFrames)`, recalculada en cada `Tick` y antes de cada pedido.»
    [Fact]
    public void Ventana_SeRecalculaAntesDeCadaPedidoSinEsperarAlTick()
    {
        using var h = new PlaybackHarness(Clip25, _ => Ms(40), prefetch: 3);
        h.Scheduler.Play();
        Assert.Equal([50], h.Source.Requests);

        // El reloj deja atrás 51 y 52 mientras se decodifica 50: el siguiente pedido ya no los incluye
        h.Clock.Advance(Frames25(3));
        h.Source.AdvanceTo(Ms(40));

        Assert.Equal([50, 53], h.Source.Requests);
    }

    // «Hay un solo pedido en vuelo, un cuadro adelantado se pide una vez por ventana»
    [Fact]
    public void Ventana_UnSoloPedidoEnVueloYCadaAdelantadoUnaVez()
    {
        using var h = new PlaybackHarness(Clip25, _ => Ms(5), prefetch: 6, budget: 2);
        for (int i = 0; i < 20; i++)
        {
            h.Refresh();
        }

        Assert.Equal(0, h.Source.OverlappingCalls);
        Assert.Equal([50, 51, 52, 53, 54, 55], h.Source.Requests.Distinct());
        for (long ahead = 51; ahead <= 55; ahead++)
        {
            Assert.Equal(1, h.Source.Requests.Count(i => i == ahead));
        }
    }

    // «los cuadros que fallan se marcan no disponibles hasta el siguiente seek.»
    [Fact]
    public void Ventana_ElCuadroQueFallaNoSeRepideHastaElSiguienteSeek()
    {
        using var h = new PlaybackHarness(Clip25, prefetch: 3);
        h.Source.Override = i => i == 100 ? throw new InvalidOperationException("roto") : Frames.Make(i, Rational.Fps25, h.Payloads);
        h.Scheduler.SeekToFrame(100);
        for (int i = 0; i < 5; i++)
        {
            h.Scheduler.Tick();
        }

        Assert.Equal(1, h.Source.Requests.Count(i => i == 100));

        h.Source.Override = null;
        h.Scheduler.SeekToFrame(100);

        Assert.Equal(2, h.Source.Requests.Count(i => i == 100));
        Assert.Equal(100, h.Scheduler.Tick().Frame!.Index);
    }

    // «Seek por épocas: cada seek cancela el token de la época anterior.»
    [Fact]
    public void Seek_CancelaElTokenDeLaEpocaAnterior()
    {
        var source = new TokenSource();
        using var cache = new FrameCache(64);
        using var scheduler = new PlaybackScheduler(Clip25, new ManualClock(Clip25.Start), source, cache, new InlineExecutor());
        CancellationToken first = source.Tokens[^1];

        scheduler.SeekToFrame(200);

        Assert.True(first.IsCancellationRequested);
        Assert.Equal(2, source.Tokens.Count);
        Assert.False(source.Tokens[^1].IsCancellationRequested);
    }

    // «Lo que llegue de una época vieja se guarda en caché pero no cuenta.»
    [Fact]
    public void Seek_LoDeUnaEpocaViejaSeGuardaPeroNoCuenta()
    {
        using var h = new PlaybackHarness(Clip25, _ => Ms(10));
        h.Source.HonorCancellation = false;
        h.Scheduler.SeekToFrame(200);
        h.Scheduler.SeekToFrame(100);

        FramePresentation p = h.SettleAndTick();

        Assert.True(h.Cache.Contains(200));
        Assert.Equal(100, p.Frame!.Index);
        Assert.Equal([100], h.ExactReady);
        Assert.Equal(0, h.Scheduler.Stats.DecodeErrors);
    }

    // «Si el decodificador devuelve un índice distinto del pedido, se descarta y cuenta como error: el seek es exacto al cuadro o no es.»
    [Fact]
    public void Seek_IndiceDistintoDelPedido_SeDescartaYCuentaComoError()
    {
        using var h = new PlaybackHarness(Clip25, prefetch: 1);
        h.Source.Override = i => Frames.Make(i == 120 ? 121 : i, Rational.Fps25, h.Payloads);

        h.Scheduler.SeekToFrame(120);
        FramePresentation p = h.Scheduler.Tick();

        Assert.False(h.Cache.Contains(121));
        Assert.False(p.IsExact);
        Assert.NotEqual(121, p.Frame?.Index);
        Assert.True(h.Scheduler.IsSeekPending);
        Assert.Equal(1, h.Scheduler.Stats.DecodeErrors);
        Assert.Equal(120, Assert.Single(h.Failures).FrameIndex);
    }

    // «un solo candado (`_gate`) que nunca se retiene mientras se decodifica»
    [Fact]
    public void Hilos_ElCandadoNoSeRetieneMientrasSeDecodifica()
    {
        var probe = new GateProbe();
        var source = new SimulatedSource(Rational.Fps25, new PayloadTracker());
        source.Override = i =>
        {
            probe.Check();
            return Frames.Make(i, Rational.Fps25);
        };
        using var cache = new FrameCache(64);
        using var scheduler = probe.Attach(
            new PlaybackScheduler(Clip25, new ManualClock(Clip25.Start), source, cache, new InlineExecutor()));

        scheduler.SeekToFrame(150);
        scheduler.Tick();

        Assert.True(probe.Checks > 0);
        Assert.Equal(0, probe.HeldCount);
    }

    // «los eventos y los `Dispose` de cuadros se hacen fuera de él.»
    [Fact]
    public void Hilos_LosEventosSeLanzanFueraDelCandado()
    {
        var probe = new GateProbe();
        using var h = new PlaybackHarness(Clip25, prefetch: 2);
        probe.Attach(h.Scheduler);
        h.Scheduler.ExactFrameReady += (_, _) => probe.Check();
        h.Scheduler.EndReached += (_, _) => probe.Check();
        h.Scheduler.DecodeFailed += (_, _) => probe.Check();
        h.Source.Override = i => i == 200 ? throw new InvalidOperationException("roto") : Frames.Make(i, Rational.Fps25, h.Payloads);

        h.Scheduler.SeekToFrame(200);
        h.Scheduler.SeekToFrame(250);
        h.Scheduler.Seek(Clip25.End - OneFrame);
        h.SettleAndTick();
        h.Scheduler.Play();
        h.Clock.Advance(Frames25(2));
        h.Scheduler.Tick();

        Assert.Equal(1, h.EndCount);
        Assert.Single(h.Failures);
        Assert.True(probe.Checks >= 4);
        Assert.Equal(0, probe.HeldCount);
    }

    // «los eventos y los `Dispose` de cuadros se hacen fuera de él.»
    [Fact]
    public void Hilos_LosCuadrosSaltadosSeLiberanFueraDelCandado()
    {
        var probe = new GateProbe();
        var source = new SimulatedSource(Rational.Fps25, new PayloadTracker());
        source.Override = i => ProbedFrame(i, probe);
        var clock = new ManualClock(Clip25.Start);
        using var cache = new FrameCache(64);
        using var scheduler = probe.Attach(new PlaybackScheduler(Clip25, clock, source, cache, new InlineExecutor()));
        scheduler.Tick();
        scheduler.Play();

        // Del 50 al 53 de un salto: 51 y 52 solo estaban en la caché y se liberan al borrar lo que ya pasó
        clock.Advance(Frames25(3));
        Assert.Equal(53, scheduler.Tick().Frame!.Index);

        Assert.True(probe.Checks >= 3);
        Assert.Equal(0, probe.HeldCount);
    }

    // «los eventos y los `Dispose` de cuadros se hacen fuera de él.»
    [Fact]
    public void Hilos_LosCuadrosExpulsadosAlGuardarSeLiberanFueraDelCandado()
    {
        var probe = new GateProbe();
        var source = new SimulatedSource(Rational.Fps25, new PayloadTracker());
        source.Override = i => ProbedFrame(i, probe);
        using var cache = new FrameCache(2);
        using var scheduler = probe.Attach(
            new PlaybackScheduler(Clip25, new ManualClock(Clip25.Start), source, cache, new InlineExecutor()));

        // Con lugar para dos cuadros, guardar los adelantados de la nueva ventana expulsa a los anteriores
        scheduler.SeekToFrame(150);

        Assert.True(probe.Checks >= 6);
        Assert.Equal(0, probe.HeldCount);
    }

    // «Si un `ValueTask` termina en otro hilo, el resultado se guarda allí y solo el bucle vuelve al ejecutor.»
    [Fact]
    public async Task Hilos_UnResultadoAsincronoSeGuardaEnSuHiloYSoloElBucleVuelveAlEjecutor()
    {
        var source = new DeferredSource();
        var executor = new QueuedExecutor();
        using var cache = new FrameCache(64);
        using var scheduler = new PlaybackScheduler(
            Clip25, new ManualClock(Clip25.Start), source, cache, executor, new PlaybackOptions { PrefetchFrames = 2 });
        executor.RunAll();
        Assert.Equal([50], source.Requests);

        await Task.Run(() => source.Pending!.SetResult(Frames.Make(50, Rational.Fps25)));

        Assert.True(cache.Contains(50));
        Assert.Equal(1, executor.Queued);
        Assert.Equal([50], source.Requests);

        executor.RunAll();
        Assert.Equal([50, 51], source.Requests);
    }

    private static Time Frames25(long count) => Time.FromFrames(count, Rational.Fps25);

    private static DecodedFrame ProbedFrame(long index, GateProbe probe)
    {
        Time pts = Time.FromFrames(index, Rational.Fps25);
        return new DecodedFrame(index, pts, OneFrame, new ProbePayload(probe), 1);
    }

    /// <summary>Mira si el hilo actual tiene el candado del programador.</summary>
    private sealed class GateProbe
    {
        private static readonly FieldInfo GateField =
            typeof(PlaybackScheduler).GetField("_gate", BindingFlags.NonPublic | BindingFlags.Instance)!;

        private Lock? _gate;

        public int Checks { get; private set; }

        public int HeldCount { get; private set; }

        public PlaybackScheduler Attach(PlaybackScheduler scheduler)
        {
            _gate = (Lock)GateField.GetValue(scheduler)!;
            return scheduler;
        }

        public void Check()
        {
            if (_gate is null)
            {
                return;
            }

            Checks++;
            if (_gate.IsHeldByCurrentThread)
            {
                HeldCount++;
            }
        }
    }

    private sealed class ProbePayload(GateProbe probe) : IDisposable
    {
        public void Dispose() => probe.Check();
    }

    private sealed class TokenSource : IVideoFrameSource
    {
        public List<CancellationToken> Tokens { get; } = [];

        public ValueTask<DecodedFrame?> GetFrameAsync(long frameIndex, CancellationToken cancellationToken)
        {
            Tokens.Add(cancellationToken);
            var pending = new TaskCompletionSource<DecodedFrame?>();
            cancellationToken.Register(() => pending.TrySetCanceled(cancellationToken));
            return new ValueTask<DecodedFrame?>(pending.Task);
        }
    }

    private sealed class DeferredSource : IVideoFrameSource
    {
        public List<long> Requests { get; } = [];

        public TaskCompletionSource<DecodedFrame?>? Pending { get; private set; }

        public ValueTask<DecodedFrame?> GetFrameAsync(long frameIndex, CancellationToken cancellationToken)
        {
            Requests.Add(frameIndex);
            Pending = new TaskCompletionSource<DecodedFrame?>();
            return new ValueTask<DecodedFrame?>(Pending.Task);
        }
    }

    private sealed class QueuedExecutor : IDecodeExecutor
    {
        private readonly Queue<Action> _queue = new();

        public int Queued
        {
            get
            {
                lock (_queue)
                {
                    return _queue.Count;
                }
            }
        }

        public void Post(Action work)
        {
            lock (_queue)
            {
                _queue.Enqueue(work);
            }
        }

        public void RunAll()
        {
            while (true)
            {
                Action work;
                lock (_queue)
                {
                    if (!_queue.TryDequeue(out work!))
                    {
                        return;
                    }
                }

                work();
            }
        }
    }
}
