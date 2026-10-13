using Rodaje.App.Services;
using Rodaje.App.Services.LongActions;
using Rodaje.App.Services.Modules;
using Rodaje.App.Tests.Support;
using Rodaje.Core.Model;

namespace Rodaje.App.Tests.Session;

/// <summary>
/// Cerrar Traducción o Texto a voz con el trabajo en marcha (tarea 281): se cancela y, hasta que termina, la barra de
/// avisos dice «Cancelando…» en la fila de la acción, como Quitar fondo.
/// </summary>
public class DialogWorkTests
{
    private static EditorSession NewSession() =>
        new(new FakePlayback(), new FakeProber(), new FakeDialogs(), new SequentialIdGenerator());

    [Fact]
    public void AlCerrarConElTrabajoEnMarcha_CancelaYDiceCancelandoEnLaFilaDeLaAccion() => UiThread.Run(async () =>
    {
        var service = new LongActionService(NewSession());
        var confirmed = new TaskCompletionSource();
        Task closing = Task.CompletedTask;
        CancellationToken token = default;
        Task<bool> run = service.RunAsync("tools.translateTrack", "Traducir pista", scope =>
        {
            var work = new DialogWork();
            token = work.Token;
            // Como el módulo: ve la cancelación y tarda en confirmarla.
            _ = work.Track(WaitForCancelAsync(confirmed.Task, work.Token));
            scope.Report(0.3, "Traduciendo…");
            closing = work.CloseAsync(service, "Traducir pista", scope);
            return closing;
        });

        Assert.True(token.IsCancellationRequested);
        LongActionStatus status = Assert.Single(service.Running);
        Assert.Equal("Traducir pista", status.Title);
        Assert.Equal(ModuleActivity.Cancelling, status.Message);
        Assert.Null(status.Progress);
        Assert.False(status.CanCancel);
        Assert.False(closing.IsCompleted);

        confirmed.SetResult();
        Assert.True(await run);
        Assert.Empty(service.Running);
    });

    /// <summary>
    /// Tarea 292: la prueba de arriba miraba <c>closing</c> antes de que volviera <c>CancelAsync</c>, así que un
    /// <c>CloseAsync</c> que pidiera la cancelación y dejara de esperar al trabajo también la pasaba. Aquí el trabajo no se
    /// registra en el token (lo mira por su cuenta, como un módulo que sondea): sin registros, <c>CancelAsync</c> vuelve
    /// ya completada y todo lo que <c>CloseAsync</c> hace después de pedirla ocurre antes de devolver el control. Además se
    /// cede el hilo varias veces, por si algo más quedara en la cola del contexto. Sin reloj: lo que termina la espera es
    /// la confirmación.
    /// </summary>
    [Fact]
    public void AlCerrarSigueEsperandoLaConfirmacionAunqueSeCedaElHilo() => UiThread.Run(async () =>
    {
        // La premisa: sin registros, CancelAsync no deja nada para otra vuelta del hilo. Si .NET la cambia, esta prueba
        // perdería su fuerza sin avisar.
        using (var probe = new CancellationTokenSource())
        {
            Assert.True(probe.CancelAsync().IsCompleted);
        }

        var service = new LongActionService(NewSession());
        var confirmed = new TaskCompletionSource();
        Task closing = Task.CompletedTask;
        CancellationToken token = default;
        Task<bool> run = service.RunAsync("tools.translateTrack", "Traducir pista", scope =>
        {
            var work = new DialogWork();
            token = work.Token;
            _ = work.Track(confirmed.Task);
            scope.Report(0.3, "Traduciendo…");
            closing = work.CloseAsync(service, "Traducir pista", scope);
            return closing;
        });

        Assert.True(token.IsCancellationRequested);
        for (int turn = 0; turn < 10; turn++)
        {
            await Task.Yield();
            Assert.False(closing.IsCompleted, $"CloseAsync terminó sin la confirmación (vuelta {turn}).");
            Assert.False(run.IsCompleted);
            LongActionStatus status = Assert.Single(service.Running);
            Assert.Equal(("Traducir pista", ModuleActivity.Cancelling), (status.Title, status.Message));
            Assert.False(status.CanCancel);
        }

        confirmed.SetResult();
        await closing;
        Assert.True(await run);
        Assert.Empty(service.Running);
    });

    [Fact]
    public void SinAccionAbreSuPropiaFilaYUnFalloDelTrabajoNoSeEscapa() => UiThread.Run(async () =>
    {
        var service = new LongActionService(NewSession());
        var confirmed = new TaskCompletionSource();
        using var work = new DialogWork();
        _ = work.Track(FailAfterAsync(confirmed.Task));

        Task closing = work.CloseAsync(service, "Texto a voz");

        Assert.Equal(("Texto a voz", ModuleActivity.Cancelling), (Assert.Single(service.Running).Title, service.Running[0].Message));
        confirmed.SetResult();
        await closing;
        Assert.Empty(service.Running);
    });

    [Fact]
    public void SinNadaEnMarchaCierraSinAviso() => UiThread.Run(async () =>
    {
        var service = new LongActionService(NewSession());
        using var work = new DialogWork();
        _ = work.Track(Task.CompletedTask);
        int changes = 0;
        service.Changed += (_, _) => changes++;

        await work.CloseAsync(service, "Texto a voz");

        Assert.True(work.Token.IsCancellationRequested);
        Assert.Equal(0, changes);
    });

    private static async Task WaitForCancelAsync(Task confirmed, CancellationToken token)
    {
        try
        {
            await Task.Delay(Timeout.Infinite, token);
        }
        catch (OperationCanceledException)
        {
            await confirmed;
        }
    }

    private static async Task FailAfterAsync(Task confirmed)
    {
        await confirmed;
        throw new InvalidOperationException("el módulo se cayó");
    }
}
