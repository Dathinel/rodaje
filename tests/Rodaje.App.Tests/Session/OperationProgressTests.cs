using Rodaje.App.Services.Inspector;
using Rodaje.App.Services.Modules;
using Rodaje.App.Services.Operations;
using Rodaje.Media.Proxies;

namespace Rodaje.App.Tests.Session;

/// <summary>B9b: el modelo común de avance (fracción, fases, tiempo restante suavizado, «último avance») sin WinUI.</summary>
public class OperationProgressTests
{
    private sealed class ManualClock : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => 1000;

        public override long GetTimestamp() => _ticks;

        public void Advance(double seconds) => _ticks += (long)(seconds * 1000);
    }

    [Fact]
    public void SinFraccion_BarraEnMovimientoYLoQueLlevaTrabajando()
    {
        var clock = new ManualClock();
        var p = new OperationProgress(clock);
        p.Report(null, "Leyendo el proyecto");
        clock.Advance(2);

        OperationSnapshot early = p.Snapshot();
        Assert.Null(early.Fraction);
        Assert.Equal("", early.PercentText);
        Assert.Equal("Leyendo el proyecto", early.Text);

        clock.Advance(1.6);
        p.Pulse();
        OperationSnapshot s = p.Snapshot();
        Assert.Equal("Leyendo el proyecto · lleva 4 s", s.Text);
        Assert.False(s.IsStalled);
        Assert.Equal("", s.StalledText);
    }

    [Fact]
    public void SinAvance_DiceQueSigueTrabajandoYElUltimoAvance()
    {
        var clock = new ManualClock();
        var p = new OperationProgress(clock);
        p.Report(0.3, "Analizando");
        clock.Advance(4.9);
        Assert.False(p.Snapshot().IsStalled);

        clock.Advance(0.2);
        OperationSnapshot stalled = p.Snapshot();
        Assert.True(stalled.IsStalled);
        Assert.Equal("Sigue trabajando; último avance hace 5 s", stalled.StalledText);
        Assert.Null(stalled.Remaining);

        // Cualquier avance (aquí, la fracción) lo retira; un mensaje nuevo o un pulso también.
        p.Report(0.31);
        Assert.False(p.Snapshot().IsStalled);
        clock.Advance(6);
        Assert.True(p.Snapshot().IsStalled);
        p.Pulse();
        Assert.False(p.Snapshot().IsStalled);
        clock.Advance(6);
        p.Report(0.31, "Casi");
        Assert.False(p.Snapshot().IsStalled);
    }

    [Fact]
    public void InformarLoMismoNoEsAvance()
    {
        var clock = new ManualClock();
        var p = new OperationProgress(clock);
        p.Report(0.5, "Mitad");
        clock.Advance(6);
        p.Report(0.5, "Mitad");
        Assert.True(p.Snapshot().IsStalled);
    }

    [Fact]
    public void Fases_PasoKDeN()
    {
        var clock = new ManualClock();
        var p = new OperationProgress(clock);
        p.SetPhase(2, 5, "Transcribir");
        p.Report(OperationProgress.PhaseFraction(2, 5, 0.5), "Clip 3 de 6");

        OperationSnapshot s = p.Snapshot();
        Assert.Equal("Paso 2 de 5: Transcribir", s.PhaseLabel);
        Assert.Equal("Paso 2 de 5: Transcribir · Clip 3 de 6", s.Text);
        Assert.Equal("30 %", s.PercentText);
        Assert.Equal(0.3, s.Fraction!.Value, 6);

        p.SetPhase(1, 1, "Guardar");
        Assert.Equal("Guardar", p.Snapshot().PhaseLabel);
        Assert.Equal(0, OperationProgress.PhaseFraction(1, 0, 0.5));
        Assert.Equal(1, OperationProgress.PhaseFraction(5, 5, 1));
    }

    [Fact]
    public void TiempoRestante_ConRitmoConstante()
    {
        var clock = new ManualClock();
        var p = new OperationProgress(clock);
        for (int i = 1; i <= 20; i++)
        {
            clock.Advance(1);
            p.Report(i * 0.01);
        }

        // 1 % por segundo: del 20 % queda el 80 %, unos 80 s.
        OperationSnapshot s = p.Snapshot();
        Assert.NotNull(s.Remaining);
        Assert.Equal(80, s.Remaining!.Value.TotalSeconds, 1);
    }

    [Fact]
    public void TiempoRestante_NoSeEstimaAlPrincipioNiAlTerminar()
    {
        var clock = new ManualClock();
        var p = new OperationProgress(clock);
        clock.Advance(1);
        p.Report(0.5);
        Assert.Null(p.Snapshot().Remaining); // menos de 3 s: ruido

        var q = new OperationProgress(clock);
        clock.Advance(4);
        q.Report(0.01);
        Assert.Null(q.Snapshot().Remaining); // menos del 2 %

        p.Complete();
        OperationSnapshot done = p.Snapshot();
        Assert.Null(done.Remaining);
        Assert.Equal("100 %", done.PercentText);
        Assert.False(done.IsStalled);
        clock.Advance(60);
        Assert.False(p.Snapshot().IsStalled);
    }

    [Fact]
    public void TiempoRestante_SuavizaUnTironDeRitmo()
    {
        var clock = new ManualClock();
        var p = new OperationProgress(clock);
        for (int i = 1; i <= 10; i++)
        {
            clock.Advance(1);
            p.Report(i * 0.02);
        }

        double before = p.Snapshot().Remaining!.Value.TotalSeconds; // 2 % por segundo: 40 s
        clock.Advance(1);
        p.Report(0.20 + 0.10); // un segundo con el 10 %: la estimación baja, pero no se desploma a 7 s
        double after = p.Snapshot().Remaining!.Value.TotalSeconds;

        Assert.Equal(40, before, 1);
        Assert.True(after < before);
        Assert.True(after > 14, $"{after}");
    }

    [Fact]
    public void FraccionQueRetrocede_ReiniciaElRitmo()
    {
        var clock = new ManualClock();
        var p = new OperationProgress(clock);
        for (int i = 1; i <= 10; i++)
        {
            clock.Advance(1);
            p.Report(i * 0.1);
        }

        clock.Advance(1);
        p.Report(0.05); // empezó otra cosa
        clock.Advance(4);
        p.Report(0.10);

        double remaining = p.Snapshot().Remaining!.Value.TotalSeconds;
        Assert.True(remaining > 60, $"{remaining}"); // 5 % en 4 s, no el 10 % por segundo de antes
    }

    [Fact]
    public void Cancelar_DiceCancelandoSinPorcentajeNiBotonYNoSeTapa()
    {
        var clock = new ManualClock();
        var p = new OperationProgress(clock);
        p.SetCancellable(true);
        p.Report(0.4, "Analizando");
        Assert.True(p.Snapshot().CanCancel);

        p.MarkCancelling();
        p.Report(0.5, "Analizando otra vez");
        p.SetCancellable(true);
        clock.Advance(20);

        OperationSnapshot s = p.Snapshot();
        Assert.True(s.IsCancelling);
        Assert.False(s.CanCancel);
        Assert.Null(s.Fraction);
        Assert.Equal("Cancelando…", s.Text);
        Assert.Equal("", s.StalledText); // «Cancelando…» ya explica la espera
    }

    [Fact]
    public void Informar_ConValoresRaros_NoLanza()
    {
        var p = new OperationProgress(new ManualClock());
        p.Report(double.NaN);
        p.Report(double.PositiveInfinity);
        p.Report(-3);
        p.Report(7);
        Assert.Equal(1.0, p.Snapshot().Fraction!.Value);
    }

    [Fact]
    public void Textos_DeTiempoEnEspanol()
    {
        Assert.Equal("4 s", OperationText.Duration(TimeSpan.FromSeconds(4)));
        Assert.Equal("1 min 5 s", OperationText.Duration(TimeSpan.FromSeconds(65)));
        Assert.Equal("2 min", OperationText.Duration(TimeSpan.FromSeconds(120)));
        Assert.Equal("1 h 03 min", OperationText.Duration(TimeSpan.FromSeconds(3780)));
        Assert.Equal("queda poco", OperationText.Remaining(TimeSpan.FromSeconds(7)));
        Assert.Equal("quedan unos 40 s", OperationText.Remaining(TimeSpan.FromSeconds(41)));
        Assert.Equal("queda cerca de 1 min", OperationText.Remaining(TimeSpan.FromSeconds(61)));
        Assert.Equal("quedan unos 2 min", OperationText.Remaining(TimeSpan.FromSeconds(100)));
        Assert.Equal("quedan unos 3 min", OperationText.Remaining(TimeSpan.FromSeconds(150)));
        Assert.Equal("quedan unas 2 h 05 min", OperationText.Remaining(TimeSpan.FromMinutes(125)));
    }

    [Fact]
    public async Task Informar_DesdeVariosHilos_NoRompeNada()
    {
        var p = new OperationProgress();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(t => Task.Run(() =>
        {
            for (int i = 0; i < 2000; i++)
            {
                p.Report(i / 2000.0, i % 100 == 0 ? $"paso {t}" : null);
                _ = p.Snapshot();
            }
        })));

        Assert.NotNull(p.Snapshot().Fraction);
    }

    [Fact]
    public void TrabajoDeModulo_LlevaSuAvanceAlModeloComun()
    {
        var list = new ModuleJobList(null);
        ModuleJob job = list.Add("Subtítulos", "Subtítulos");
        Assert.True(job.Operation.Snapshot().CanCancel);

        job.SetRunning();
        job.Report(0.4, "Transcribiendo");
        OperationSnapshot s = job.Operation.Snapshot();
        Assert.Equal("40 %", s.PercentText);
        Assert.Equal("Transcribiendo", s.Message);

        job.Cancel();
        s = job.Operation.Snapshot();
        Assert.True(s.IsCancelling);
        Assert.Equal("Cancelando…", s.Text);

        job.Finish(ModuleJobState.Cancelled);
        Assert.True(job.Operation.Snapshot().IsFinished);
    }

    [Fact]
    public void CaptureNext_EntregaElSiguienteTrabajoDeEsteHiloYSoloUno()
    {
        var list = new ModuleJobList(null);
        var captured = new List<ModuleJob>();
        ModuleJob first;
        using (ModuleJobList.CaptureNext(captured.Add))
        {
            first = list.Add("Uno", "M");
            list.Add("Dos", "M");
        }

        list.Add("Tres", "M");
        Assert.Same(first, Assert.Single(captured));

        using (ModuleJobList.CaptureNext(captured.Add))
        {
        }

        list.Add("Cuatro", "M");
        Assert.Single(captured);
    }

    [Fact]
    public void FilaDeProxy_LlevaSuAvance_YEnColaNoDiceSigueTrabajando()
    {
        var jobs = new ProxyJobList();
        jobs.Apply(new ProxyStatus("a.mp4", ProxyState.Queued));
        OperationSnapshot queued = jobs.Rows[0].Operation!.Snapshot();
        Assert.Equal("En cola", queued.Message);

        jobs.Apply(new ProxyStatus("a.mp4", ProxyState.Generating, Progress: 0.42));
        OperationSnapshot generating = jobs.Rows[0].Operation!.Snapshot();
        Assert.Equal(0.42, generating.Fraction!.Value, 6);

        jobs.Apply(new ProxyStatus("a.mp4", ProxyState.Ready));
        Assert.Null(jobs.Rows[0].Operation);
    }

    [Fact]
    public void EnEspera_NoSeDiceSigueTrabajando()
    {
        var clock = new ManualClock();
        var p = new OperationProgress(clock);
        p.Report(null, "En cola");
        p.SetWaiting(true);
        clock.Advance(60);
        Assert.False(p.Snapshot().IsStalled);

        p.Report(0.1, "Empezó");
        clock.Advance(6);
        Assert.True(p.Snapshot().IsStalled);
    }
}
