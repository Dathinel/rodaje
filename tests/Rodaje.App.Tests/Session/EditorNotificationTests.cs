using Rodaje.App.Services;
using Rodaje.App.Tests.Support;
using Rodaje.Core.Model;

namespace Rodaje.App.Tests.Session;

public class EditorNotificationTests
{
    [Fact]
    public void UnAvisoConBoton_LlevaElTextoYLaAccion() => UiThread.Run(() =>
    {
        var session = new EditorSession(new FakePlayback(), new FakeProber(), new FakeDialogs(), new SequentialIdGenerator());
        var received = new List<EditorNotification>();
        session.Notified += (_, n) => received.Add(n);
        int runs = 0;

        session.Notify(NotificationSeverity.Informational, "Atajos", "Se intercambiaron", new NotificationAction("Deshacer", () => runs++));
        session.Notify(NotificationSeverity.Warning, "Atajos", "Sin botón");

        Assert.Equal(2, received.Count);
        Assert.Equal("Deshacer", received[0].Action?.Text);
        received[0].Action!.Run();
        Assert.Equal(1, runs);
        Assert.Null(received[1].Action);
    });
}
