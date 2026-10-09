using Rodaje.Core.Timing;
using Rodaje.Playback.Tests.Support;
using static Rodaje.Playback.Tests.Support.PlaybackHarness;

namespace Rodaje.Playback.Tests;

public class PlaybackSchedulerTests
{
    // 25 fps, 0,5 s en la línea de tiempo, origen [2 s, 12 s): cuadros 50 a 299
    private static readonly ClipTiming Clip25 =
        new(Time.FromSeconds(0.5), Time.FromSeconds(2), Time.FromSeconds(12), Rational.Fps25);

    [Fact]
    public void Constructor_ValidaArgumentos()
    {
        var clock = new ManualClock();
        var source = new SimulatedSource(Rational.Fps25, new PayloadTracker());
        using var cache = new FrameCache(10);
        var executor = new InlineExecutor();
        Assert.Throws<ArgumentNullException>(() => new PlaybackScheduler(Clip25, null!, source, cache, executor));
        Assert.Throws<ArgumentNullException>(() => new PlaybackScheduler(Clip25, clock, null!, cache, executor));
        Assert.Throws<ArgumentNullException>(() => new PlaybackScheduler(Clip25, clock, source, null!, executor));
        Assert.Throws<ArgumentNullException>(() => new PlaybackScheduler(Clip25, clock, source, cache, null!));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PlaybackScheduler(Clip25, clock, source, cache, executor, new PlaybackOptions { PrefetchFrames = 0 }));
    }

    [Fact]
    public void AlCrear_PideLaVentanaDesdeLaPosicionDelReloj()
    {
        using var h = new PlaybackHarness(Clip25, prefetch: 4);
        Assert.Equal([50, 51, 52, 53], h.Source.Requests);
        Assert.Equal(Clip25, h.Scheduler.Timing);
        Assert.False(h.Scheduler.IsPlaying);
        Assert.False(h.Scheduler.IsSeekPending);

        FramePresentation first = h.Scheduler.Tick();
        Assert.True(first.IsExact);
        Assert.Equal(50, first.Frame!.Index);
        Assert.Equal(Time.FromSeconds(2), first.SourceTime);
        Assert.Equal(1, h.Scheduler.Stats.CacheHits);
    }

    [Fact]
    public void Seek_ConDecodificadorLento_MantieneElCuadroAnteriorHastaTenerElExacto()
    {
        using var h = new PlaybackHarness(Clip25, _ => Ms(30));
        h.SettleAndTick();

        h.Scheduler.Seek(Time.FromSeconds(4.51));
        Assert.True(h.Scheduler.IsSeekPending);
        Assert.Equal(Time.FromSeconds(4.51), h.Clock.Position);
        FramePresentation waiting = h.Scheduler.Tick();
        Assert.False(waiting.IsExact);
        Assert.Equal(50, waiting.Frame!.Index);
        Assert.Equal(150, waiting.TargetFrame);
        Assert.Empty(h.ExactReady);

        h.Source.AdvanceTo(h.WallTime + Ms(30));
        Assert.Equal([150], h.ExactReady);
        Assert.False(h.Scheduler.IsSeekPending);
        FramePresentation exact = h.Scheduler.Tick();
        Assert.True(exact.IsExact);
        Assert.Equal(150, exact.Frame!.Index);
        Assert.Equal(Time.FromFrames(150, Rational.Fps25), exact.Frame.Pts);
        Assert.Equal(0, h.Scheduler.Stats.LateRefreshes);
    }

    [Fact]
    public void Seek_AUnCuadroYaEnCache_AvisaEnElActo()
    {
        using var h = new PlaybackHarness(Clip25);
        h.SettleAndTick();
        h.Scheduler.SeekToFrame(53);
        Assert.Equal([53], h.ExactReady);
        Assert.Equal(53, h.Scheduler.Tick().Frame!.Index);
    }

    [Fact]
    public void Seek_FueraDelClip_SeAcotaAlPrimerYUltimoCuadro()
    {
        using var h = new PlaybackHarness(Clip25);
        h.Scheduler.Seek(Time.FromSeconds(-3));
        Assert.Equal(Clip25.Start, h.Clock.Position);
        Assert.Equal(50, h.SettleAndTick().Frame!.Index);

        h.Scheduler.Seek(Time.FromSeconds(1_000));
        Assert.Equal(Clip25.End - Time.FromTicks(1), h.Clock.Position);
        Assert.Equal(299, h.SettleAndTick().Frame!.Index);

        h.Scheduler.SeekToFrame(-5);
        Assert.Equal(50, h.SettleAndTick().Frame!.Index);
        h.Scheduler.SeekToFrame(long.MaxValue);
        Assert.Equal(299, h.SettleAndTick().Frame!.Index);
    }

    [Fact]
    public void Seek_CancelaLaDecodificacionEnCursoYNoLaMuestra()
    {
        using var h = new PlaybackHarness(Clip25, _ => Ms(10));
        h.Scheduler.SeekToFrame(200);
        Assert.True(h.Source.IsBusy);

        h.Scheduler.SeekToFrame(100);

        Assert.Equal(100, h.Source.Requests[^1]);
        Assert.Equal(100, h.SettleAndTick().Frame!.Index);
        Assert.Equal([100], h.ExactReady);
        Assert.Equal(0, h.Source.OverlappingCalls);
    }

    [Fact]
    public void Seek_SiElDecodificadorIgnoraLaCancelacion_GuardaElCuadroViejoSinMostrarlo()
    {
        using var h = new PlaybackHarness(Clip25, _ => Ms(10));
        h.Source.HonorCancellation = false;
        h.Scheduler.SeekToFrame(200);
        h.Scheduler.SeekToFrame(100);
        Assert.True(h.Source.IsBusy);

        FramePresentation p = h.SettleAndTick();

        Assert.Equal(100, p.Frame!.Index);
        Assert.True(h.Cache.Contains(200));
        Assert.Equal([100], h.ExactReady);
        Assert.Equal(0, h.Source.OverlappingCalls);
    }

    [Fact]
    public void Reproduccion_PausaYReanudacion_NoCuentanTardiosNiDescartados()
    {
        using var h = new PlaybackHarness(Clip25, _ => Ms(5));
        h.SettleAndTick();
        h.Scheduler.Play();
        Assert.True(h.Scheduler.IsPlaying);
        for (int i = 0; i < 60; i++)
        {
            Assert.True(h.Refresh().IsExact);
        }

        h.Scheduler.Pause();
        Time paused = h.Scheduler.Position;
        long shown = h.Scheduler.Tick().Frame!.Index;
        for (int i = 0; i < 120; i++)
        {
            FramePresentation still = h.Refresh();
            Assert.Equal(shown, still.Frame!.Index);
        }

        Assert.Equal(paused, h.Scheduler.Position);
        h.Scheduler.Play();
        for (int i = 0; i < 60; i++)
        {
            Assert.True(h.Refresh().IsExact);
        }

        PlaybackStats stats = h.Scheduler.Stats;
        Assert.Equal(0, stats.LateRefreshes);
        Assert.Equal(0, stats.FramesDropped);
        Assert.Equal(h.Scheduler.Tick().Frame!.Index - 50 + 1, stats.FramesShown);
        Assert.True(stats.MaxAvOffset < Time.FromFrames(1, Rational.Fps25));
    }

    [Fact]
    public void FinDelClip_PausaElRelojMuestraElUltimoCuadroYAvisaUnaVez()
    {
        var timing = new ClipTiming(Time.Zero, Time.Zero, Time.FromSeconds(1), Rational.Fps25);
        using var h = new PlaybackHarness(timing, _ => Ms(5));
        h.Scheduler.Play();
        for (int i = 0; i < 90; i++)
        {
            h.Refresh();
        }

        Assert.False(h.Scheduler.IsPlaying);
        Assert.Equal(1, h.EndCount);
        FramePresentation last = h.Scheduler.Tick();
        Assert.Equal(24, last.Frame!.Index);
        Assert.True(last.IsExact);
        Assert.Equal(1, h.EndCount);

        h.Scheduler.Play();
        Assert.Equal(Time.Zero, h.Clock.Position);
        Assert.True(h.Scheduler.IsPlaying);
        for (int i = 0; i < 90; i++)
        {
            h.Refresh();
        }

        Assert.Equal(2, h.EndCount);
    }

    [Fact]
    public void DecodificadorConPicoDeLatencia_RepiteYLuegoSeRecuperaDescartando()
    {
        // El cuadro 70 tarda 600 ms (15 cuadros a 25 fps); el resto, 4 ms
        using var h = new PlaybackHarness(Clip25, i => i == 70 ? Ms(600) : Ms(4));
        h.SettleAndTick();
        h.Scheduler.Play();

        var shown = new List<long>();
        var exact = new List<bool>();
        for (int i = 0; i < 180; i++)
        {
            FramePresentation p = h.Refresh();
            shown.Add(p.Frame!.Index);
            exact.Add(p.IsExact);
        }

        Assert.Equal(shown.Order(), shown);
        int stall = shown.IndexOf(69);
        int repeats = shown.Count(i => i == 69);
        Assert.True(repeats > 10, $"Se esperaba repetir el cuadro 69 durante el pico; se repitió {repeats} veces.");
        Assert.True(exact.Skip(stall + repeats + 3).All(e => e), "Tras el pico debía volver a la sincronía exacta.");

        PlaybackStats stats = h.Scheduler.Stats;
        Assert.True(stats.LateRefreshes > 0);
        Assert.True(stats.FramesDropped > 0);
        Assert.Equal(shown[^1] - 50 + 1, stats.FramesShown + stats.FramesDropped);
    }

    [Fact]
    public void DecodificadorMasLentoQueElVideo_AvanzaSinDerivaIlimitada()
    {
        // 60 ms por cuadro: 16,7 cuadros por segundo para un video de 25
        using var h = new PlaybackHarness(Clip25, _ => Ms(60));
        h.SettleAndTick();
        h.Scheduler.Play();

        long previous = 0;
        for (int i = 0; i < 400; i++)
        {
            FramePresentation p = h.Refresh();
            Assert.True(p.Frame!.Index >= previous);
            previous = p.Frame.Index;
        }

        PlaybackStats stats = h.Scheduler.Stats;
        Assert.True(stats.LateRefreshes > 100);
        Assert.True(stats.FramesDropped > 30);
        // Cota: un cuadro de atraso al pedirlo + latencia + un refresco + latencia mientras se repite
        Assert.True(stats.MaxAvOffset < Ms(2 * 60 + 40 + 17), $"Desfase máximo {stats.MaxAvOffset}.");
    }

    [Fact]
    public void ErrorDeDecodificacion_SeInformaYElCuadroSeSalta()
    {
        using var h = new PlaybackHarness(Clip25, prefetch: 3);
        h.Source.Override = i => i == 152 ? throw new InvalidOperationException("fallo inyectado") : Frames.Make(i, Rational.Fps25, h.Payloads);
        h.Scheduler.SeekToFrame(151);
        Assert.Equal(151, h.Scheduler.Tick().Frame!.Index);

        Assert.Single(h.Failures);
        Assert.Equal(152, h.Failures[0].FrameIndex);
        Assert.Equal("fallo inyectado", h.Failures[0].Error.Message);
        Assert.Equal(1, h.Scheduler.Stats.DecodeErrors);
        Assert.Equal([151, 152, 153], h.Source.Requests[^3..]);

        h.Scheduler.SeekToFrame(152);
        FramePresentation p = h.SettleAndTick();
        Assert.Equal(151, p.Frame!.Index);
        Assert.False(p.IsExact);
        Assert.Equal(2, h.Scheduler.Stats.DecodeErrors);
    }

    [Fact]
    public void ErrorAsincrono_SeInformaIgual()
    {
        using var h = new PlaybackHarness(Clip25, _ => Ms(5), prefetch: 2);
        h.Source.Override = i => i == 120 ? throw new InvalidOperationException("fallo inyectado") : Frames.Make(i, Rational.Fps25, h.Payloads);
        h.Scheduler.SeekToFrame(120);
        h.Source.Settle();
        Assert.Equal(120, Assert.Single(h.Failures).FrameIndex);
        Assert.True(h.Cache.Contains(121));
        Assert.True(h.Scheduler.IsSeekPending);
    }

    [Fact]
    public void CuadroEquivocadoONulo_NoSeGuardan()
    {
        using var h = new PlaybackHarness(Clip25, prefetch: 3);
        h.Source.Override = i => i switch
        {
            61 => Frames.Make(62, Rational.Fps25, h.Payloads),
            62 => null,
            _ => Frames.Make(i, Rational.Fps25, h.Payloads),
        };
        h.Scheduler.SeekToFrame(60);

        Assert.True(h.Cache.Contains(60));
        Assert.False(h.Cache.Contains(61));
        Assert.False(h.Cache.Contains(62));
        DecodeFailedEventArgs failure = Assert.Single(h.Failures);
        Assert.Equal(61, failure.FrameIndex);
        Assert.IsType<InvalidOperationException>(failure.Error);
        int requests = h.Source.Requests.Count;
        h.Scheduler.Tick();
        Assert.Equal(requests, h.Source.Requests.Count);
    }

    [Fact]
    public void CacheMasChicaQueLaVentana_NoSeQuedaDecodificandoSinFin()
    {
        using var h = new PlaybackHarness(Clip25, _ => Ms(3), prefetch: 10, budget: 2);
        h.Scheduler.SeekToFrame(100);
        FramePresentation p = h.SettleAndTick();
        Assert.Equal(100, p.Frame!.Index);
        Assert.True(h.Source.Requests.Count < 40);

        h.Scheduler.Play();
        for (int i = 0; i < 100; i++)
        {
            Assert.NotNull(h.Refresh().Frame);
        }
    }

    [Fact]
    public void RelojMovidoHaciaAtrasSinSeek_ElProgramadorLoSigue()
    {
        using var h = new PlaybackHarness(Clip25);
        h.Scheduler.SeekToFrame(200);
        Assert.Equal(200, h.SettleAndTick().Frame!.Index);
        h.Clock.Seek(Clip25.TimelineStartOf(80));
        h.Scheduler.Tick();
        Assert.Equal(80, h.Scheduler.Tick().Frame!.Index);
    }

    [Fact]
    public void ResetStats_PoneTodoEnCero()
    {
        using var h = new PlaybackHarness(Clip25, _ => Ms(60));
        h.Scheduler.Play();
        for (int i = 0; i < 30; i++)
        {
            h.Refresh();
        }

        Assert.NotEqual(default, h.Scheduler.Stats);
        h.Scheduler.ResetStats();
        Assert.Equal(default, h.Scheduler.Stats);
    }

    [Fact]
    public void Dispose_LiberaElCuadroMostradoYDescartaLoQueLlegueDespues()
    {
        var h = new PlaybackHarness(Clip25, _ => Ms(10));
        h.Source.HonorCancellation = false;
        h.Scheduler.SeekToFrame(70);
        h.Source.Settle();
        FramePresentation p = h.Scheduler.Tick();
        h.Scheduler.SeekToFrame(90);
        Assert.True(h.Source.IsBusy);

        h.Scheduler.Dispose();
        h.Scheduler.Dispose();
        Assert.True(h.Cache.Contains(70));
        Assert.False(p.Frame!.IsReleased);
        h.Source.Settle();
        Assert.False(h.Cache.Contains(90));
        h.Cache.Dispose();
        Assert.Equal(0, h.Payloads.Live);
        Assert.True(p.Frame.IsReleased);

        Assert.Throws<ObjectDisposedException>(() => h.Scheduler.Tick());
        Assert.Throws<ObjectDisposedException>(() => h.Scheduler.Seek(Time.Zero));
        Assert.Throws<ObjectDisposedException>(() => h.Scheduler.Play());
        Assert.Throws<ObjectDisposedException>(() => h.Scheduler.Pause());
    }
}
