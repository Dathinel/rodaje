using Rodaje.App.Services;
using Rodaje.App.Services.LongActions;
using Rodaje.App.Tests.Effects;
using Rodaje.App.Tests.Support;
using Rodaje.Core.Commands;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Tests.Session;

/// <summary>Las acciones largas: en marcha desde el clic, sin doble arranque, con avance, cancelar y el aviso final con «Deshacer».</summary>
public class LongActionServiceTests
{
    private static EditorSession NewSession() =>
        new(new FakePlayback(), new FakeProber(), new FakeDialogs(), new SequentialIdGenerator());

    [Fact]
    public void EnMarchaDesdeElClicYSinDobleArranque() => UiThread.Run(async () =>
    {
        var service = new LongActionService(NewSession());
        int changes = 0;
        service.Changed += (_, _) => changes++;
        var gate = new TaskCompletionSource();

        Task<bool> first = service.RunAsync("analysis.autocolor", "Color automático", _ => gate.Task);
        Assert.True(service.IsRunning("analysis.autocolor"));
        Assert.Equal(1, changes);
        Assert.Equal("Color automático", service.Current!.Title);
        Assert.Null(service.Current.Progress);

        // El segundo clic no arranca otra.
        int ran = 0;
        Assert.False(await service.RunAsync("analysis.autocolor", "Color automático", _ => { ran++; return Task.CompletedTask; }));
        Assert.Equal(0, ran);
        Assert.Single(service.Running);

        gate.SetResult();
        Assert.True(await first);
        Assert.False(service.IsRunning("analysis.autocolor"));
        Assert.Null(service.Current);
        Assert.Equal(2, changes);
    });

    [Fact]
    public void ElAvanceSeRecortaYNoDespiertaPorMenosDeMedioPunto()
    {
        var service = new LongActionService(NewSession());
        int changes = 0;
        LongActionScope scope = service.TryBegin("a", "A")!;
        service.Changed += (_, _) => changes++;

        scope.Report(0.421, "Analizando…");
        Assert.Equal("42 %", service.Current!.ProgressText);
        Assert.Equal("Analizando…", service.Current.Message);
        scope.Report(0.423);
        Assert.Equal(1, changes);
        scope.Progress.Report(1.7);
        Assert.Equal(1, service.Current.Progress);
        Assert.Equal("100 %", service.Current.ProgressText);
        scope.Report(double.NaN);
        Assert.Null(service.Current.Progress);
        Assert.Equal("", service.Current.ProgressText);
        Assert.Equal(3, changes);

        scope.Dispose();
        scope.Report(0.5);
        Assert.Null(service.Current);
    }

    [Fact]
    public void LaMasRecienteEsLaQueSeMuestra()
    {
        var service = new LongActionService(NewSession());
        using LongActionScope a = service.TryBegin("a", "A")!;
        LongActionScope b = service.TryBegin("b", "B")!;
        Assert.Equal("B", service.Current!.Title);
        Assert.Equal(["a", "b"], service.Running.Select(r => r.ActionId));
        b.Dispose();
        Assert.Equal("A", service.Current!.Title);
    }

    [Fact]
    public void CancelarSoloSiLaAccionLoAdmite() => UiThread.Run(async () =>
    {
        EditorSession session = NewSession();
        var notices = new List<EditorNotification>();
        session.Notified += (_, n) => notices.Add(n);
        var service = new LongActionService(session);

        Task<bool> fixedWork = service.RunAsync("fija", "Fija", async s => await Task.Delay(50, s.Token));
        Assert.False(service.Cancel("fija"));
        Assert.True(await fixedWork);

        Task<bool> run = service.RunAsync("color", "Color automático", async s => await Task.Delay(Timeout.Infinite, s.Token), canCancel: true);
        Assert.True(service.Current!.CanCancel);
        Assert.True(service.Cancel("color"));
        Assert.True(await run);
        Assert.False(service.IsRunning("color"));
        EditorNotification last = notices[^1];
        Assert.Equal(("Color automático", "Cancelado."), (last.Title, last.Message));
    });

    [Fact]
    public void UnFalloQuedaComoAvisoYLaAccionTermina() => UiThread.Run(async () =>
    {
        EditorSession session = NewSession();
        EditorNotification? notice = null;
        session.Notified += (_, n) => notice = n;
        var service = new LongActionService(session, _ => { });

        Assert.True(await service.RunAsync("x", "Estabilizar", _ => throw new InvalidOperationException("No se pudo leer el video.")));

        Assert.Equal(NotificationSeverity.Error, notice!.Severity);
        Assert.Equal("Estabilizar", notice.Title);
        Assert.False(service.IsRunning("x"));
    });

    [Fact]
    public void SiCambioElProyectoYNadieAvisoQuedaListoConDeshacer() => UiThread.Run(async () =>
    {
        EditorSession session = await EffectCommandsTests.TwoClips();
        EditorNotification? notice = null;
        session.Notified += (_, n) => notice = n;
        var service = new LongActionService(session);
        int steps = session.History.Count;

        await service.RunAsync("analysis.beats", "Detectar ritmo", _ =>
        {
            session.Execute(Edits.AddBeatMarkers(session.SequenceId, [Time.FromSeconds(1), Time.FromSeconds(2)]));
            return Task.CompletedTask;
        });

        Assert.Equal(steps + 1, session.History.Count);
        Assert.Equal(NotificationSeverity.Success, notice!.Severity);
        Assert.Equal("Detectar ritmo", notice.Title);
        Assert.Contains("Marcadores de ritmo", notice.Message, StringComparison.Ordinal);
        Assert.Equal("Deshacer", notice.Action!.Text);
        notice.Action.Run();
        Assert.True(session.CanRedo);
        Assert.Equal("Marcadores de ritmo", session.RedoDescription);
    });

    [Fact]
    public void SiLaAccionYaAvisoNoSeRepiteNiSinCambios() => UiThread.Run(async () =>
    {
        EditorSession session = await EffectCommandsTests.TwoClips();
        var notices = new List<EditorNotification>();
        session.Notified += (_, n) => notices.Add(n);
        var service = new LongActionService(session);

        // Cancelado en el diálogo: no cambió nada y no hay aviso.
        await service.RunAsync("a", "Quitar silencios", _ => Task.CompletedTask);
        Assert.Empty(notices);

        // La acción deja su aviso con «Deshacer»: queda ese, sin otro encima.
        await service.RunAsync("b", "Color automático", _ =>
        {
            session.Execute(Edits.AddBeatMarkers(session.SequenceId, [Time.FromSeconds(1)]));
            session.Notify(NotificationSeverity.Success, "Color automático", "Temperatura +3 %.", new NotificationAction("Deshacer", () => session.Undo()));
            return Task.CompletedTask;
        });
        Assert.Single(notices);
        Assert.Equal("Temperatura +3 %.", notices[0].Message);
    });
}
