using Rodaje.App.Services;
using Rodaje.App.Services.LongActions;
using Rodaje.App.Services.Modules;
using Rodaje.App.Tests.Support;
using Rodaje.Core.Model;

namespace Rodaje.App.Tests.Modules;

/// <summary>
/// Tarea 239 (decisión del 13-oct): cancelar un módulo puede tardar hasta 10 s y arrancarlo hasta 30 s, siempre que la
/// interfaz lo diga («Iniciando…», «Cancelando…») con la barra en movimiento. Se prueba la lógica, sin WinUI.
/// </summary>
public class ModuleActivityTests
{
    [Fact]
    public void Fila_AlCancelar_DiceCancelandoHastaQueElModuloLoConfirma()
    {
        var list = new ModuleJobList(null);
        int changes = 0;
        list.Changed += (_, _) => changes++;
        ModuleJob job = list.Add("Subtítulos", "Subtítulos");
        job.SetRunning();
        job.Report(0.4, "Transcribiendo");
        int before = changes;

        job.Cancel();

        // Mientras dura la gracia (el módulo aún no confirmó): «Cancelando…», barra en movimiento y la fila avisó.
        Assert.Equal(ModuleJobState.Running, job.State);
        Assert.True(job.IsCancelling);
        Assert.Equal(ModuleActivity.Cancelling, job.StatusText);
        Assert.True(job.IsIndeterminate);
        Assert.True(changes > before);

        job.Finish(ModuleJobState.Cancelled);
        Assert.False(job.IsCancelling);
        Assert.False(job.IsIndeterminate);
        Assert.Equal("Cancelado", job.StatusText);
    }

    [Fact]
    public void Fila_CanceladaPorQuienLaLanzo_TambienDiceCancelando()
    {
        var list = new ModuleJobList(null);
        using var external = new CancellationTokenSource();
        ModuleJob job = list.Add("Quitar fondo", "Visión", external.Token);
        job.SetRunning();

        external.Cancel();

        Assert.Equal(ModuleActivity.Cancelling, job.StatusText);
        Assert.True(job.IsIndeterminate);
    }

    [Fact]
    public void Fila_MientrasArranca_BarraEnMovimiento()
    {
        var list = new ModuleJobList(null);
        ModuleJob job = list.Add("Reducir ruido", "Audio");
        job.SetWaiting(ModuleActivity.Starting);

        Assert.Equal(ModuleActivity.Starting, job.StatusText);
        Assert.True(job.IsIndeterminate);
        job.SetRunning();
        Assert.False(job.IsIndeterminate);
    }

    [Fact]
    public void Dialogo_IniciandoTrabajandoYCancelando()
    {
        ModuleActivityView starting = ModuleActivity.Describe(null, cancelRequested: false, "Transcribiendo");
        Assert.Equal(ModuleActivity.Starting, starting.Text);
        Assert.True(starting.IsIndeterminate);

        var list = new ModuleJobList(null);
        ModuleJob job = list.Add("Subtítulos", "Subtítulos");
        job.SetRunning();
        job.Report(0.25, null);
        ModuleActivityView working = ModuleActivity.Describe(job, cancelRequested: false, "Transcribiendo");
        Assert.Equal("Transcribiendo…", working.Text);
        Assert.Equal(0.25, working.Progress);
        Assert.False(working.IsIndeterminate);

        // El diálogo pidió cancelar antes de que el módulo arrancara o mientras corre.
        Assert.Equal(new ModuleActivityView(ModuleActivity.Cancelling, 0, true), ModuleActivity.Describe(null, cancelRequested: true, "Transcribiendo"));
        ModuleActivityView cancelling = ModuleActivity.Describe(job, cancelRequested: true, "Transcribiendo");
        Assert.Equal(ModuleActivity.Cancelling, cancelling.Text);
        Assert.True(cancelling.IsIndeterminate);
    }

    [Fact]
    public void BarraDeAvisos_AlCancelar_DiceCancelandoSinBotonYLosAvancesNoLoTapan() => UiThread.Run(async () =>
    {
        var service = new LongActionService(NewSession());
        var gate = new TaskCompletionSource();
        Task<bool> run = service.RunAsync("subtitles.auto", "Subtítulos automáticos", async scope =>
        {
            scope.Report(0.4, "Transcribiendo la voz…");
            await gate.Task;
        }, canCancel: true);

        Assert.True(service.Cancel("subtitles.auto"));

        LongActionStatus status = service.Current!;
        Assert.Equal(ModuleActivity.Cancelling, status.Message);
        Assert.Null(status.Progress);
        Assert.False(status.CanCancel);

        gate.SetResult();
        await run;
        Assert.Null(service.Current);
    });

    [Fact]
    public void BarraDeAvisos_TrasCerrarElDialogo_CancelandoHastaQueTermina() => UiThread.Run(async () =>
    {
        var service = new LongActionService(NewSession());
        var gate = new TaskCompletionSource();

        Task waiting = service.ShowCancellingAsync("Quitar fondo", gate.Task);

        LongActionStatus status = Assert.Single(service.Running);
        Assert.Equal("Quitar fondo", status.Title);
        Assert.Equal(ModuleActivity.Cancelling, status.Message);
        Assert.Null(status.Progress);
        Assert.False(status.CanCancel);

        gate.SetResult();
        await waiting;
        Assert.Empty(service.Running);
    });

    [Fact]
    public void BarraDeAvisos_ConLaAccionEnMarcha_UsaSuPropiaFila() => UiThread.Run(async () =>
    {
        var service = new LongActionService(NewSession());
        var gate = new TaskCompletionSource();
        Task waiting = Task.CompletedTask;
        Task<bool> run = service.RunAsync("analysis.noise", "Reducir ruido", scope =>
        {
            scope.Report(0.5, "Limpiando la voz…");
            waiting = service.ShowCancellingAsync("Reducir ruido", gate.Task, scope);
            scope.Report(0.6, "Limpiando la voz…");
            return waiting;
        });

        LongActionStatus status = Assert.Single(service.Running);
        Assert.Equal(ModuleActivity.Cancelling, status.Message);
        Assert.Null(status.Progress);

        gate.SetResult();
        Assert.True(await run);
        Assert.Empty(service.Running);
    });

    private static EditorSession NewSession() =>
        new(new FakePlayback(), new FakeProber(), new FakeDialogs(), new SequentialIdGenerator());
}
