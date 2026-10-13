using Rodaje.App.Services;
using Rodaje.App.Services.Crashes;
using Rodaje.App.Services.LongActions;
using Rodaje.App.Tests.Support;
using Rodaje.App.Views.Analysis;
using Rodaje.Core.Model;

namespace Rodaje.App.Tests.Analysis;

/// <summary>
/// «Color automático» y «Balance de blancos automático» con el archivo del clip ausente (tarea 293): el aviso es el texto
/// del error, ya escrito para el usuario, y no el genérico de <see cref="UnexpectedErrorNotice"/>; nada va al informe de
/// fallos ni cambia el proyecto. Un error inesperado (tarea 301), en cambio, no se atrapa ahí: sigue al aviso genérico y
/// al informe.
/// </summary>
public class AutoColorFailureNoticeTests
{
    [Theory]
    [InlineData(true, "Color automático")]
    [InlineData(false, "Balance de blancos automático")]
    public void SinElArchivoDelClipAvisaConSuTextoYNoVaAlInforme(bool levels, string title) => UiThread.Run(async () =>
    {
        string path = Path.Combine(Path.GetTempPath(), "rodaje-falta-" + Guid.NewGuid().ToString("N") + ".mp4");
        var session = new EditorSession(new FakePlayback(), new FakeProber().Video(path, 10), new FakeDialogs(), new SequentialIdGenerator());
        await session.OpenFilesAsync([Path.GetFullPath(path)]);
        session.Select([session.MainTrack!.Clips[0].Id]);
        int steps = session.History.Count;
        var notices = new List<EditorNotification>();
        session.Notified += (_, n) => notices.Add(n);
        var reported = new List<Exception>();
        var service = new LongActionService(session, reported.Add);

        Assert.True(await service.RunAsync("color", title, scope => AutoColorAction.RunAsync(session, levels, scope), canCancel: true));

        EditorNotification notice = Assert.Single(notices);
        Assert.Equal(NotificationSeverity.Warning, notice.Severity);
        Assert.Equal(title, notice.Title);
        Assert.StartsWith("No existe el archivo de medio", notice.Message, StringComparison.Ordinal);
        Assert.NotEqual(UnexpectedErrorNotice.Describe(null), notice.Message);
        Assert.Empty(reported);
        Assert.Equal(steps, session.History.Count);
        Assert.False(service.IsRunning("color"));
    });

    /// <summary>
    /// Un error que no es de «no se puede medir» (aquí un <see cref="ArgumentException"/> de la ruta vacía del medio, que
    /// <c>Av.OpenInput</c> lanza antes de mirar el disco) no lo atrapa <see cref="AutoColorAction"/>: llega a
    /// <see cref="LongActionService"/>, que lo manda al informe de fallos y avisa con el texto genérico.
    /// </summary>
    [Theory]
    [InlineData(true, "Color automático")]
    [InlineData(false, "Balance de blancos automático")]
    public void UnErrorInesperadoVaAlAvisoGenericoYAlInforme(bool levels, string title) => UiThread.Run(async () =>
    {
        string path = Path.Combine(Path.GetTempPath(), "rodaje-vacio-" + Guid.NewGuid().ToString("N") + ".mp4");
        var session = new EditorSession(new FakePlayback(), new FakeProber().Video(path, 10), new FakeDialogs(), new SequentialIdGenerator());
        await session.OpenFilesAsync([Path.GetFullPath(path)]);
        Clip clip = session.MainTrack!.Clips[0];
        session.Select([clip.Id]);
        session.Project.FindMedia(clip.MediaId)!.Path = "";
        int steps = session.History.Count;
        var notices = new List<EditorNotification>();
        session.Notified += (_, n) => notices.Add(n);
        var reported = new List<Exception>();
        var service = new LongActionService(session, reported.Add);

        Assert.True(await service.RunAsync("color", title, scope => AutoColorAction.RunAsync(session, levels, scope), canCancel: true));

        Exception error = Assert.Single(reported);
        Assert.IsType<ArgumentException>(error);
        EditorNotification notice = Assert.Single(notices);
        Assert.Equal(NotificationSeverity.Error, notice.Severity);
        Assert.Equal(title, notice.Title);
        Assert.Equal(UnexpectedErrorNotice.Describe(null), notice.Message);
        Assert.Equal(steps, session.History.Count);
        Assert.False(service.IsRunning("color"));
    });
}
