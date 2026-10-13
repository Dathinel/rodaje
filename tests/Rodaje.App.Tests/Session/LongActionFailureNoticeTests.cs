using Rodaje.App.Services;
using Rodaje.App.Services.Crashes;
using Rodaje.App.Services.LongActions;
using Rodaje.App.Tests.Support;
using Rodaje.Core.Commands;
using Rodaje.Core.Model;

namespace Rodaje.App.Tests.Session;

/// <summary>
/// Una acción larga que falla (tarea 281): el aviso es el texto fijo en español de <see cref="UnexpectedErrorNotice"/>, con
/// el título de la acción; el mensaje de la excepción no llega a la ventana, sino al informe.
/// </summary>
public class LongActionFailureNoticeTests
{
    private static EditorSession NewSession() =>
        new(new FakePlayback(), new FakeProber(), new FakeDialogs(), new SequentialIdGenerator());

    [Fact]
    public void UnFalloAvisaConTextoFijoYLlevaLaExcepcionAlInforme() => UiThread.Run(async () =>
    {
        EditorSession session = NewSession();
        var notices = new List<EditorNotification>();
        session.Notified += (_, n) => notices.Add(n);
        var reported = new List<Exception>();
        var service = new LongActionService(session, reported.Add);
        var error = new InvalidOperationException("Object reference not set to an instance of an object.");

        Assert.True(await service.RunAsync("traducir", "Traducir pista", _ => throw error));

        EditorNotification notice = Assert.Single(notices);
        Assert.Equal(NotificationSeverity.Error, notice.Severity);
        Assert.Equal("Traducir pista", notice.Title);
        Assert.Equal(UnexpectedErrorNotice.Describe(null), notice.Message);
        Assert.DoesNotContain(error.Message, notice.Message, StringComparison.Ordinal);
        Assert.Same(error, Assert.Single(reported));
        Assert.False(service.IsRunning("traducir"));
    });

    [Fact]
    public void CancelarNoEsUnFalloNiVaAlInforme() => UiThread.Run(async () =>
    {
        EditorSession session = NewSession();
        var notices = new List<EditorNotification>();
        session.Notified += (_, n) => notices.Add(n);
        var reported = new List<Exception>();
        var service = new LongActionService(session, reported.Add);

        Task<bool> run = service.RunAsync("voz", "Texto a voz", scope => Task.Delay(Timeout.Infinite, scope.Token), canCancel: true);
        Assert.True(service.Cancel("voz"));
        Assert.True(await run);

        Assert.Equal(("Texto a voz", "Cancelado."), (Assert.Single(notices).Title, notices[0].Message));
        Assert.Empty(reported);
    });
}
