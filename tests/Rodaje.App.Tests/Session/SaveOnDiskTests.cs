using Rodaje.App.Services;
using Rodaje.App.Tests.Support;
using Rodaje.Core.Commands;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;

namespace Rodaje.App.Tests.Session;

/// <summary>
/// Guardar en un disco que no colabora: el mismo proyecto abierto en dos ventanas, un archivo de solo lectura y nombres que
/// Windows no admite. Nunca se pierde en silencio lo que guardó la otra ventana ni sale una excepción del comando.
/// </summary>
public class SaveOnDiskTests
{
    private static (EditorSession Session, List<EditorNotification> Notices) NewSession(FakeDialogs? dialogs = null)
    {
        var session = new EditorSession(new FakePlayback(), new FakeProber(), dialogs ?? new FakeDialogs(), new SequentialIdGenerator());
        var notices = new List<EditorNotification>();
        session.Notified += (_, n) => notices.Add(n);
        return (session, notices);
    }

    private static async Task<EditorSession> Open(string path)
    {
        (EditorSession session, _) = NewSession(new FakeDialogs { OpenPath = path });
        Assert.True(await session.OpenProjectAsync());
        return session;
    }

    [Fact]
    public void DosVentanasConElMismoProyecto_LaSegundaNoPisaLoDeLaPrimeraSinPreguntar() => UiThread.Run(async () =>
    {
        using var dir = new TempDirectory();
        string path = dir.File("compartido.rodaje");
        (EditorSession first, _) = NewSession(new FakeDialogs { SavePath = path });
        Assert.True(await first.SaveAsync());

        EditorSession a = await Open(path);
        (EditorSession b, List<EditorNotification> notices) = NewSession(new FakeDialogs { OpenPath = path });
        Assert.True(await b.OpenProjectAsync());

        a.Execute(new RenameProjectCommand("De la ventana A"));
        Assert.True(await a.SaveAsync());
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));

        b.Execute(new RenameProjectCommand("De la ventana B"));
        Assert.False(await b.SaveAsync());
        Assert.Equal("De la ventana A", ProjectSerializer.Load(path).Name);
        EditorNotification notice = notices[^1];
        Assert.Equal(NotificationSeverity.Warning, notice.Severity);
        Assert.True(b.IsDirty);

        // «Guardar encima» es decisión del usuario: entonces sí escribe.
        Assert.NotNull(notice.Action);
        notice.Action!.Run();
        await WaitUntil(() => !b.IsDirty);
        Assert.Equal("De la ventana B", ProjectSerializer.Load(path).Name);

        // Y su propio guardado siguiente ya no se ve como un cambio ajeno.
        b.Execute(new RenameProjectCommand("Otra vez B"));
        Assert.True(await b.SaveAsync());
    });

    [Fact]
    public void ArchivoDeSoloLectura_AvisaYSigueConCambios() => UiThread.Run(async () =>
    {
        using var dir = new TempDirectory();
        string path = dir.File("bloqueado.rodaje");
        (EditorSession session, List<EditorNotification> notices) = NewSession(new FakeDialogs { SavePath = path });
        Assert.True(await session.SaveAsync());
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            session.Execute(new RenameProjectCommand("Cambiado"));
            Assert.False(await session.SaveAsync());
            Assert.Contains(notices, n => n.Title == "No se pudo guardar");
            Assert.True(session.IsDirty);
            Assert.Equal(path, session.FilePath);
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    });

    [Theory]
    [InlineData("con\0nulo")]
    [InlineData("tubo|raro")]
    [InlineData("dos:puntos")]
    [InlineData("pregunta?")]
    [InlineData("CON")]
    [InlineData("nul")]
    [InlineData("COM1")]
    public void NombresQueWindowsNoAdmite_AvisanSinLanzar(string name) => UiThread.Run(async () =>
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var dir = new TempDirectory();
        (EditorSession session, List<EditorNotification> notices) = NewSession();
        session.Execute(new RenameProjectCommand("x"));
        Assert.False(await session.SaveAsAsync(Path.Combine(dir.Path, name)));
        Assert.Contains(notices, n => n.Title == "No se pudo guardar");
        Assert.Null(session.FilePath);
        Assert.Empty(Directory.EnumerateFileSystemEntries(dir.Path));
    });

    private static async Task WaitUntil(Func<bool> done)
    {
        for (int i = 0; i < 200 && !done(); i++)
        {
            await Task.Delay(25);
        }

        Assert.True(done());
    }
}
