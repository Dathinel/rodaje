using Rodaje.App.Services;
using Rodaje.App.Services.Modules;
using Rodaje.App.Tests.Effects;
using Rodaje.App.Tests.Support;
using Rodaje.Core.Commands;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Tests.Modules;

public sealed class ModuleLogicTests
{
    [Fact]
    public void Catalogo_lee_vram_gana_la_version_mayor_e_informa_los_rotos()
    {
        using EchoModules a = new EchoModules().Add("vision", vramMb: 1500, version: "1.0.0").Add("roto");
        using EchoModules b = new EchoModules().Add("vision", vramMb: 900, version: "1.2.0");
        File.Delete(Path.Combine(a.Root, "roto", "Rodaje.Module.Echo.exe"));
        var problems = new List<string>();

        IReadOnlyList<ModuleDescriptor> modules = ModuleCatalog.Scan(
            [new ModuleRoot(a.Root, ModuleOrigin.Installed), new ModuleRoot(b.Root, ModuleOrigin.Bundled)], problems);

        ModuleDescriptor vision = Assert.Single(modules);
        Assert.Equal("1.2.0", vision.Version);
        Assert.Equal(900, vision.VramMb);
        Assert.True(vision.UsesGpu);
        Assert.True(vision.Offers("contar"));
        Assert.Equal(ModuleOrigin.Bundled, vision.Origin);
        Assert.Contains(problems, p => p.Contains("roto", StringComparison.Ordinal));
    }

    [Fact]
    public void En_desarrollo_encuentra_el_modulo_eco_del_repositorio()
    {
        string? dev = ModuleCatalog.FindDevelopmentModules(AppContext.BaseDirectory);
        Assert.NotNull(dev);
        ModuleDescriptor eco = Assert.Single(ModuleCatalog.Scan([new ModuleRoot(dev, ModuleOrigin.Development)]), m => m.Id == "rodaje-eco");
        Assert.True(File.Exists(eco.EntryPath));
        Assert.False(eco.UsesGpu);
    }

    [Fact]
    public void Presupuesto_un_pesado_a_la_vez_y_la_suma_dentro_de_cinco_octavos()
    {
        var budget = new VramBudget(4L << 30);
        Assert.Equal(2560, budget.BudgetMb);
        Assert.True(budget.TryReserve("cpu", 0));
        Assert.True(budget.TryReserve("subtitulos", 1500));
        Assert.True(budget.TryReserve("subtitulos", 1500));
        Assert.False(budget.TryReserve("vision", 1200));
        Assert.Equal(["subtitulos"], budget.Blocking(1200));
        Assert.True(budget.TryReserve("ruido", 500));
        Assert.False(budget.TryReserve("otro", 600));
        Assert.Equal(2000, budget.UsedMb);

        Task changed = budget.Changed;
        budget.Release("subtitulos");
        Assert.True(changed.IsCompleted);
        Assert.True(budget.TryReserve("vision", 1200));
    }

    [Fact]
    public void Presupuesto_uno_que_no_cabe_corre_solo_y_sin_VRAM_conocida_solo_cuenta_el_pesado()
    {
        var small = new VramBudget(2L << 30);
        Assert.True(small.TryReserve("traducir", 2500));
        Assert.False(small.TryReserve("ruido", 10));
        small.Release("traducir");

        var unknown = new VramBudget(0);
        Assert.Null(unknown.BudgetMb);
        Assert.True(unknown.TryReserve("a", 3000));
        Assert.True(unknown.TryReserve("b", 900));
        Assert.False(unknown.TryReserve("c", 1024));
        Assert.Empty(unknown.Blocking(500));
    }

    [Fact]
    public void Fila_de_trabajo_estima_lo_que_queda_y_avisa_al_terminar()
    {
        var list = new ModuleJobList(null);
        int changes = 0;
        ModuleJob? finished = null;
        list.Changed += (_, _) => changes++;
        list.JobFinished += (_, j) => finished = j;
        ModuleJob job = list.Add("Subtítulos", "Subtítulos");
        Assert.Equal("En espera", job.StatusText);
        job.SetRunning();
        Thread.Sleep(50);
        job.Report(0.5, "Transcribiendo");
        Assert.NotNull(job.Remaining);
        Assert.StartsWith("Transcribiendo · 50 %", job.StatusText, StringComparison.Ordinal);
        Assert.Equal(1, list.ActiveCount);
        job.Finish(ModuleJobState.Completed);
        Assert.Same(job, finished);
        Assert.Equal(1, job.Progress);
        Assert.Equal("Terminado", job.StatusText);
        Assert.True(changes >= 3);
        job.Cancel();
        Assert.Equal(ModuleJobState.Completed, job.State);
    }

    [Fact]
    public void Una_propuesta_se_ve_antes_y_se_aplica_en_un_solo_paso() => UiThread.Run(async () =>
    {
        EditorSession session = await EffectCommandsTests.TwoClips();
        var proposals = new ModuleProposals(session);
        int steps = session.History.Count;
        Time[] beats = [Time.FromSeconds(1), Time.FromSeconds(2), Time.FromSeconds(3)];
        var proposal = new ModuleProposal("Marcas de ritmo", session.SequenceId,
            [.. beats.Select(t => new PreviewMark(PreviewMarkKind.Marker, t, t, "pulso"))],
            project => [.. beats.Select(t => (ICommand)Edits.AddMarker(session.SequenceId, t, "pulso"))]);

        proposals.Show(proposal);
        Assert.Same(proposal, proposals.Pending);
        Assert.Empty(session.Sequence.Markers);

        Assert.True(proposals.Apply());
        Assert.Null(proposals.Pending);
        Assert.Equal(3, session.Sequence.Markers.Count);
        Assert.Equal(steps + 1, session.History.Count);
        Assert.Equal("Marcas de ritmo", session.UndoDescription);

        session.Undo();
        Assert.Empty(session.Sequence.Markers);
    });

    [Fact]
    public void Una_propuesta_que_ya_no_se_puede_aplicar_avisa_y_no_toca_el_proyecto() => UiThread.Run(async () =>
    {
        EditorSession session = await EffectCommandsTests.TwoClips();
        var proposals = new ModuleProposals(session);
        int steps = session.History.Count;
        EditorNotification? notified = null;
        session.Notified += (_, n) => notified = n;

        proposals.Show(new ModuleProposal("Cortar", session.SequenceId, [],
            _ => [Edits.Split(session.SequenceId, "no-existe", Time.FromSeconds(1))]));
        Assert.False(proposals.Apply());
        Assert.NotNull(notified);
        Assert.Equal(steps, session.History.Count);

        proposals.Show(new ModuleProposal("Otra", "secuencia-borrada", [], _ => []));
        Assert.False(proposals.Apply());

        proposals.Show(new ModuleProposal("Descartada", session.SequenceId, [], _ => throw new InvalidOperationException()));
        proposals.Discard();
        Assert.Null(proposals.Pending);
        Assert.False(proposals.Apply());
    });

    [Fact]
    public void Los_tiempos_del_modulo_se_llevan_a_la_secuencia_con_la_velocidad_del_clip()
    {
        var clip = new Clip { Id = "c", MediaId = "m", Start = Time.FromSeconds(10), SourceIn = Time.FromSeconds(4), SourceOut = Time.FromSeconds(14) };
        Rational fps = Rational.Fps30;

        Assert.Equal(Time.FromSeconds(11), ModuleTime.ToSequence(clip, Time.FromSeconds(4), 1.0, fps));
        Assert.Equal(Time.FromSeconds(12).SnapToFrame(fps), ModuleTime.ToSequence(clip, Time.FromSeconds(5), 1.01, fps));
        Assert.Null(ModuleTime.ToSequence(clip, Time.FromSeconds(4), 11, fps));
        Assert.Null(ModuleTime.ToSequence(clip, Time.Zero, 1, fps));
        Assert.Null(ModuleTime.ToSequence(clip, Time.FromSeconds(4), double.NaN, fps));
    }
}
