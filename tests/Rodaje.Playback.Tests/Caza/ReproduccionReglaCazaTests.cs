using Rodaje.Core.Timing;
using Rodaje.Playback.Tests.Support;
using static Rodaje.Playback.Tests.Support.PlaybackHarness;

namespace Rodaje.Playback.Tests.Caza;

/// <summary>Las promesas de «La regla» en <c>docs/arquitectura/reproduccion.md</c>.</summary>
public class ReproduccionReglaCazaTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);

    // 25 fps, origen [0 s, 10 s): cuadros 0 a 249
    private static readonly ClipTiming Clip25 =
        new(Time.Zero, Time.Zero, Time.FromSeconds(10), Rational.Fps25);

    // «El video lee la posición de un reloj maestro (IAudioClock) y nunca lo empuja.»
    [Fact]
    public void ElVideoLeeElRelojYNuncaLoEmpuja()
    {
        var clock = new RecordingClock();
        var payloads = new PayloadTracker();
        var source = new SimulatedSource(Rational.Fps25, payloads) { Latency = _ => Ms(100) };
        using var cache = new FrameCache(64);
        using var scheduler = new PlaybackScheduler(Clip25, clock, source, cache, new InlineExecutor());

        scheduler.Play();
        Time wall = Time.Zero;
        for (int i = 0; i < 90; i++)
        {
            wall += Refresh60;
            clock.Advance(Refresh60);
            source.AdvanceTo(wall);
            FramePresentation shown = scheduler.Tick();
            Assert.Equal(clock.Position, scheduler.Position);
            Assert.Equal(Clip25.ToSource(clock.Position), shown.SourceTime);
        }

        Assert.Equal(1, clock.Starts);
        Assert.Equal(0, clock.Pauses);
        Assert.Equal(0, clock.Seeks);
        source.Settle();
    }

    // «Si el equipo no alcanza, se repite [...] un cuadro»
    [Fact]
    public void SiElDecodificadorNoAlcanza_RepiteElUltimoCuadro()
    {
        using var h = new PlaybackHarness(Clip25, _ => Ms(200), prefetch: 4);
        h.Source.Settle();
        Assert.Equal(0, h.Scheduler.Tick().Frame!.Index);
        h.Scheduler.Play();

        DecodedFrame? previous = null;
        bool repeated = false;
        for (int i = 0; i < 30; i++)
        {
            FramePresentation shown = h.Refresh();
            Assert.NotNull(shown.Frame);
            if (!shown.IsExact)
            {
                Assert.True(shown.Frame.Index < shown.TargetFrame);
                repeated |= ReferenceEquals(previous, shown.Frame);
            }

            previous = shown.Frame;
        }

        Assert.True(repeated);
        Assert.True(h.Scheduler.Stats.LateRefreshes > 0);
    }

    // «Si el equipo no alcanza, se [...] salta un cuadro»
    [Fact]
    public void SiElRenderNoAlcanza_SaltaCuadros()
    {
        using var h = new PlaybackHarness(Clip25, prefetch: 8);
        h.Source.Settle();
        Assert.Equal(0, h.Scheduler.Tick().Frame!.Index);
        h.Scheduler.Play();

        // Un refresco que llega 120 ms tarde: el reloj pasó por los cuadros 1 y 2 sin que se pintaran.
        FramePresentation shown = h.Refresh(Ms(120));

        Assert.Equal(3, shown.TargetFrame);
        Assert.True(shown.IsExact);
        Assert.Equal(3, shown.Frame!.Index);
        Assert.Equal(2, h.Scheduler.Stats.FramesDropped);
    }

    // «[...] nunca una muestra de audio»
    [Fact]
    public void ConElDecodificadorAtrasado_ElRelojNoPierdeNiRepiteTiempo()
    {
        using var h = new PlaybackHarness(Clip25, _ => Ms(500), prefetch: 4);
        h.Scheduler.Play();

        for (int i = 1; i <= 120; i++)
        {
            h.Refresh();
            Assert.Equal(Time.FromTicks(Refresh60.Ticks * i), h.Clock.Position);
        }

        Assert.True(h.Scheduler.IsPlaying);
        Assert.True(h.Scheduler.Stats.LateRefreshes > 0);
    }

    // «Nada en el camino de render espera al decodificador.» (decodificación que nunca termina)
    [Fact]
    public void Tick_NoEsperaUnaDecodificacionPendiente()
    {
        var clock = new ManualClock();
        var source = new NeverSource();
        using var cache = new FrameCache(64);
        using var scheduler = new PlaybackScheduler(Clip25, clock, source, cache, new InlineExecutor());

        scheduler.Play();
        for (int i = 0; i < 10; i++)
        {
            clock.Advance(Refresh60);
            FramePresentation shown = scheduler.Tick();
            Assert.Null(shown.Frame);
            Assert.False(shown.IsExact);
        }

        Assert.Equal(1, source.Calls);
    }

    // «Nada en el camino de render espera al decodificador.» (decodificador bloqueado en su hilo)
    [Fact]
    public async Task Tick_NoEsperaAlHiloDeDecodificacionBloqueado()
    {
        using var release = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        var clock = new ManualClock();
        var source = new BlockingSource(entered, release, Limit);
        using var cache = new FrameCache(64);
        var thread = new DecodeThread("Rodaje caza regla", Limit);
        var scheduler = new PlaybackScheduler(Clip25, clock, source, cache, thread);
        try
        {
            Assert.True(entered.Wait(Limit));
            scheduler.Play();

            Task ticks = Task.Run(() =>
            {
                for (int i = 0; i < 30; i++)
                {
                    clock.Advance(Refresh60);
                    Assert.Null(scheduler.Tick().Frame);
                }
            });

            Task finished = await Task.WhenAny(ticks, Task.Delay(Limit));
            Assert.Same(ticks, finished);
            await ticks;
            Assert.Equal(1, source.Calls);
        }
        finally
        {
            release.Set();
            scheduler.Dispose();
            thread.Dispose();
        }
    }

    private sealed class RecordingClock : IAudioClock
    {
        private readonly ManualClock _inner = new();

        public int Starts { get; private set; }

        public int Pauses { get; private set; }

        public int Seeks { get; private set; }

        public Time Position => _inner.Position;

        public bool IsRunning => _inner.IsRunning;

        public void Start()
        {
            Starts++;
            _inner.Start();
        }

        public void Pause()
        {
            Pauses++;
            _inner.Pause();
        }

        public void Seek(Time position)
        {
            Seeks++;
            _inner.Seek(position);
        }

        public void Advance(Time delta) => _inner.Advance(delta);
    }

    private sealed class NeverSource : IVideoFrameSource
    {
        public int Calls { get; private set; }

        public ValueTask<DecodedFrame?> GetFrameAsync(long frameIndex, CancellationToken cancellationToken)
        {
            Calls++;
            return new ValueTask<DecodedFrame?>(new TaskCompletionSource<DecodedFrame?>().Task);
        }
    }

    private sealed class BlockingSource(ManualResetEventSlim entered, ManualResetEventSlim release, TimeSpan limit)
        : IVideoFrameSource
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public ValueTask<DecodedFrame?> GetFrameAsync(long frameIndex, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                entered.Set();
                release.Wait(limit, CancellationToken.None);
            }

            return new ValueTask<DecodedFrame?>((DecodedFrame?)null);
        }
    }
}
