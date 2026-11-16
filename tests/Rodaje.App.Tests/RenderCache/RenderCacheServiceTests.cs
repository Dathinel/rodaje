using Rodaje.App.Services.RenderCache;
using Rodaje.Core.Timing;
using Rodaje.Engine.RenderCache;
using Rodaje.Export.Running;
using Xunit;

namespace Rodaje.App.Tests.RenderCache;

public sealed class RenderCacheServiceTests
{
    private const long Gb = 1024L * 1024 * 1024;

    [Fact]
    public void Arranca_tras_tres_segundos_de_reposo_y_uno_a_la_vez()
    {
        (RenderCacheService service, Clock clock, Worker worker) = Create();
        service.SetPlan(1, [new(0, 120, RenderCostClass.Heavy)], 0, Rational.Fps30);

        clock.Advance(TimeSpan.FromSeconds(2.9));
        service.Tick();
        Assert.Empty(worker.Started);
        Assert.Equal(RenderCacheState.Waiting, service.State);

        clock.Advance(TimeSpan.FromSeconds(0.1));
        service.Tick();
        service.Tick();
        Assert.Single(worker.Started);
        Assert.Equal(RenderCacheState.Working, service.State);

        worker.Finish(published: true);
        service.Tick();
        Assert.Equal(2, worker.Started.Count);
        Assert.Equal(1, service.Completed);
    }

    [Theory]
    [InlineData(24, 48)]
    [InlineData(30, 60)]
    [InlineData(60, 120)]
    public void Parte_los_tramos_en_trozos_de_dos_segundos(long fps, int chunk)
    {
        (RenderCacheService service, _, _) = Create();
        service.SetPlan(1, [new(10, 10 + (2 * chunk), RenderCostClass.Heavy)], 0, new Rational(fps, 1));

        Assert.Equal([new(10, chunk, 1), new(10 + chunk, chunk, 1)], service.Pending);
    }

    [Theory]
    [InlineData(24)]
    [InlineData(30)]
    [InlineData(60)]
    public void Ningun_trozo_queda_mas_corto_de_lo_que_la_cobertura_usa(long fps)
    {
        var rate = new Rational(fps, 1);
        long shortest = CacheCoverage.ShortestSpan(rate);
        int chunk = (int)(2 * fps);
        (RenderCacheService service, _, _) = Create();

        // Un resto de 5 cuadros tras dos trozos llenos y un cuadro tarde suelto en medio de lo ligero.
        service.SetPlan(1,
        [
            new(0, 10, RenderCostClass.Light), new(10, 10 + (2 * chunk) + 5, RenderCostClass.Heavy),
            new(10 + (2 * chunk) + 5, 1000, RenderCostClass.Light), new(1000, 1001, RenderCostClass.Heavy),
            new(1001, 1100, RenderCostClass.Light),
        ], 0, rate);

        Assert.All(service.Pending, w => Assert.InRange(w.Count, shortest, chunk));
        Assert.Equal((2 * chunk) + 5, service.Pending.Where(w => w.FirstFrame < 1000 - shortest).Sum(w => w.Count));
        RenderCacheWork single = service.Pending.Single(w => w.FirstFrame + w.Count > 1000 && w.FirstFrame <= 1000);
        Assert.Equal(1000, single.FirstFrame);
    }

    [Fact]
    public void Un_tramo_corto_al_final_se_ensancha_hacia_atras()
    {
        (RenderCacheService service, _, _) = Create();
        service.SetPlan(1, [new(0, 299, RenderCostClass.Light), new(299, 300, RenderCostClass.Heavy)], 0, Rational.Fps30);

        Assert.Equal([new(300 - CacheCoverage.ShortestSpan(Rational.Fps30), (int)CacheCoverage.ShortestSpan(Rational.Fps30), 1)], service.Pending);
    }

    [Fact]
    public void Apagar_con_un_trozo_en_pausa_lo_corta_y_no_lo_reanuda()
    {
        (RenderCacheService service, Clock clock, Worker worker) = Create();
        service.SetPlan(1, [new(0, 120, RenderCostClass.Heavy)], 0, Rational.Fps30);
        Start(service, clock);
        service.NotifyPlaying(true);

        service.Mode = RenderCacheMode.Off;
        service.NotifyPlaying(false);
        clock.Advance(TimeSpan.FromSeconds(3));
        service.Tick();

        Assert.True(worker.Token.IsCancellationRequested);
        Assert.True(worker.Pause!.IsPauseRequested);
        Assert.Empty(service.Pending);
    }

    [Fact]
    public void Un_trozo_que_falla_no_se_repite_sin_fin()
    {
        (RenderCacheService service, Clock clock, Worker worker) = Create();
        service.SetPlan(1, [new(0, 120, RenderCostClass.Heavy)], 0, Rational.Fps30);
        Start(service, clock);
        RenderCacheWork failed = worker.Started[0];

        worker.Fail();
        service.Tick();

        Assert.DoesNotContain(failed, service.Pending);
        Assert.Equal(1, service.Failed);
        Assert.Equal(2, worker.Started.Count);
        Assert.NotEqual(failed, worker.Started[^1]);
    }

    [Fact]
    public void Rojo_antes_que_amarillo_y_lo_mas_cercano_al_cursor_primero()
    {
        (RenderCacheService service, _, _) = Create();
        service.SetPlan(1,
        [
            new(0, 10, RenderCostClass.Tight), new(100, 110, RenderCostClass.Heavy), new(400, 410, RenderCostClass.Heavy),
            new(500, 510, RenderCostClass.Light), new(300, 310, RenderCostClass.Tight),
        ], cursorFrame: 350, Rational.Fps30);

        Assert.Equal([400L, 100, 300, 0], service.Pending.Select(w => w.FirstFrame));
    }

    [Fact]
    public void Reproducir_y_arrastrar_pausan_y_el_reposo_reanuda()
    {
        (RenderCacheService service, Clock clock, Worker worker) = Create();
        service.SetPlan(1, [new(0, 60, RenderCostClass.Heavy)], 0, Rational.Fps30);
        Start(service, clock);

        service.NotifyPlaying(true);
        Assert.True(worker.Pause!.IsPauseRequested);
        Assert.Equal(RenderCacheState.Paused, service.State);
        clock.Advance(TimeSpan.FromSeconds(10));
        service.Tick();
        Assert.True(worker.Pause.IsPauseRequested);

        service.NotifyPlaying(false);
        clock.Advance(TimeSpan.FromSeconds(2));
        service.Tick();
        Assert.True(worker.Pause.IsPauseRequested);
        clock.Advance(TimeSpan.FromSeconds(1));
        service.Tick();
        Assert.False(worker.Pause.IsPauseRequested);

        service.NotifyScrubbing(true);
        Assert.True(worker.Pause.IsPauseRequested);
        service.NotifyScrubbing(false);
        clock.Advance(TimeSpan.FromSeconds(3));
        service.Tick();
        Assert.False(worker.Pause.IsPauseRequested);
    }

    [Fact]
    public void Editar_no_pausa_pero_retrasa_el_siguiente()
    {
        (RenderCacheService service, Clock clock, Worker worker) = Create();
        service.SetPlan(1, [new(0, 120, RenderCostClass.Heavy)], 0, Rational.Fps30);
        Start(service, clock);

        service.NotifyEdited();
        Assert.False(worker.Pause!.IsPauseRequested);
        worker.Finish(published: true);
        clock.Advance(TimeSpan.FromSeconds(1));
        service.Tick();
        Assert.Single(worker.Started);
        clock.Advance(TimeSpan.FromSeconds(2));
        service.Tick();
        Assert.Equal(2, worker.Started.Count);
    }

    [Fact]
    public void Lo_de_una_revision_vieja_se_descarta()
    {
        (RenderCacheService service, Clock clock, Worker worker) = Create();
        service.SetPlan(1, [new(0, 60, RenderCostClass.Heavy)], 0, Rational.Fps30);
        Start(service, clock);

        service.SetPlan(2, [new(0, 60, RenderCostClass.Heavy)], 0, Rational.Fps30);
        worker.Finish(published: true);
        service.Tick();

        Assert.Equal(1, service.Discarded);
        Assert.Equal(0, service.Completed);
        Assert.Equal(2, worker.Started[^1].Revision);
    }

    [Fact]
    public void Manual_solo_lo_pedido_y_apagado_nada()
    {
        (RenderCacheService service, Clock clock, Worker worker) = Create();
        service.Mode = RenderCacheMode.Manual;
        service.SetPlan(1, [new(0, 60, RenderCostClass.Heavy)], 0, Rational.Fps30);
        Assert.Empty(service.Pending);
        service.Request(200, 230);
        Assert.Equal([new RenderCacheWork(200, 30, 1)], service.Pending);

        service.Mode = RenderCacheMode.Off;
        service.SetPlan(1, [new(0, 60, RenderCostClass.Heavy)], 0, Rational.Fps30);
        service.Request(0, 30);
        clock.Advance(TimeSpan.FromSeconds(5));
        service.Tick();
        Assert.Empty(worker.Started);
        Assert.Equal(RenderCacheState.Idle, service.State);
    }

    [Theory]
    [InlineData(50, 100)]
    [InlineData(10, 4)]
    public void Bloqueado_por_el_tope_o_el_disco_libre(long usedGb, long freeGb)
    {
        (RenderCacheService service, Clock clock, Worker worker) = Create(used: usedGb * Gb, free: freeGb * Gb, cap: 50 * Gb);
        service.SetPlan(1, [new(0, 60, RenderCostClass.Heavy)], 0, Rational.Fps30);
        clock.Advance(TimeSpan.FromSeconds(3));
        service.Tick();

        Assert.Empty(worker.Started);
        Assert.Equal(RenderCacheState.Blocked, service.State);
    }

    [Fact]
    public void Cancelar_corta_el_trabajo_y_vacia_la_cola()
    {
        (RenderCacheService service, Clock clock, Worker worker) = Create();
        service.SetPlan(1, [new(0, 240, RenderCostClass.Heavy)], 0, Rational.Fps30);
        Start(service, clock);

        service.Cancel();

        Assert.True(worker.Token.IsCancellationRequested);
        Assert.Empty(service.Pending);
        worker.Finish(published: false);
        service.Tick();
        Assert.Equal(RenderCacheState.Idle, service.State);
    }

    [Fact]
    public void Un_trozo_cortado_por_una_pausa_larga_vuelve_delante()
    {
        (RenderCacheService service, Clock clock, Worker worker) = Create();
        service.SetPlan(1, [new(0, 120, RenderCostClass.Heavy)], 0, Rational.Fps30);
        Start(service, clock);
        RenderCacheWork cut = worker.Started[0];
        service.NotifyPlaying(true);

        worker.Cancel();
        service.Tick();

        Assert.Equal(cut, service.Pending.First());
        Assert.Equal(2, service.Pending.Count);
        Assert.Equal(0, service.Discarded);
        service.NotifyPlaying(false);
        clock.Advance(TimeSpan.FromSeconds(3));
        service.Tick();
        Assert.Equal(cut, worker.Started[^1]);
    }

    [Fact]
    public void El_trozo_en_curso_que_sigue_vigente_se_adopta_y_no_se_encola_otra_vez()
    {
        (RenderCacheService service, Clock clock, Worker worker) = Create();
        service.SetPlan(1, [new(0, 120, RenderCostClass.Heavy)], 0, Rational.Fps30);
        Start(service, clock);
        Assert.Equal(new RenderCacheWork(0, 60, 1), worker.Started[0]);

        service.SetPlan(2, [new(0, 120, RenderCostClass.Heavy)], 0, Rational.Fps30, work => work.FirstFrame == 0);

        Assert.Equal([new RenderCacheWork(60, 60, 2)], service.Pending);
        Assert.Equal(new RenderCacheWork(0, 60, 2), service.Current);
        worker.Finish(published: true);
        service.Tick();
        Assert.Equal((1, 0), (service.Completed, service.Discarded));
    }

    [Fact]
    public void El_trozo_en_curso_que_ya_no_vale_no_se_adopta()
    {
        (RenderCacheService service, Clock clock, Worker worker) = Create();
        service.SetPlan(1, [new(0, 120, RenderCostClass.Heavy)], 0, Rational.Fps30);
        Start(service, clock);

        service.SetPlan(2, [new(0, 120, RenderCostClass.Heavy)], 0, Rational.Fps30, _ => false);

        Assert.Equal([new RenderCacheWork(0, 60, 2), new RenderCacheWork(60, 60, 2)], service.Pending);
        worker.Finish(published: true);
        service.Tick();
        Assert.Equal((0, 1), (service.Completed, service.Discarded));
    }

    [Fact]
    public void Un_tramo_corto_pedido_tras_el_ultimo_tramo_pesado_se_encola_donde_se_pidio()
    {
        (RenderCacheService service, _, _) = Create();
        service.SetPlan(1, [new(0, 100, RenderCostClass.Heavy)], 0, Rational.Fps30, frameCount: 3000);
        service.Cancel();

        service.Request(2000, 2005);

        RenderCacheWork work = Assert.Single(service.Pending);
        Assert.Equal(2000, work.FirstFrame);
        Assert.Equal(CacheCoverage.ShortestSpan(Rational.Fps30), work.Count);
    }

    private static void Start(RenderCacheService service, Clock clock)
    {
        clock.Advance(TimeSpan.FromSeconds(3));
        service.Tick();
    }

    private static (RenderCacheService, Clock, Worker) Create(long used = 0, long free = 100 * Gb, long cap = 50 * Gb)
    {
        var clock = new Clock();
        var worker = new Worker();
        return (new RenderCacheService(worker, clock, () => free, () => used, cap), clock, worker);
    }

    private sealed class Worker : IRenderCacheWorker
    {
        private TaskCompletionSource<bool>? _done;

        public List<RenderCacheWork> Started { get; } = [];

        public ExportPauseToken? Pause { get; private set; }

        public CancellationToken Token { get; private set; }

        public Task<bool> RunAsync(RenderCacheWork work, ExportPauseToken pause, CancellationToken cancellationToken)
        {
            Started.Add(work);
            Pause = pause;
            Token = cancellationToken;
            _done = new TaskCompletionSource<bool>();
            return _done.Task;
        }

        public void Finish(bool published) => _done!.SetResult(published);

        public void Cancel() => _done!.SetCanceled();

        public void Fail() => _done!.SetException(new InvalidOperationException("ffmpeg.exe no arranca"));
    }

    private sealed class Clock : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _ticks;

        public void Advance(TimeSpan by) => _ticks += by.Ticks;
    }
}
