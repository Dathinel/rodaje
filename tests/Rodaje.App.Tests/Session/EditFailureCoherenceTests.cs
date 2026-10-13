using Rodaje.App.Services;
using Rodaje.App.Services.Crashes;
using Rodaje.App.Tests.Support;
using Rodaje.Core.Commands;
using Rodaje.Core.Model;

namespace Rodaje.App.Tests.Session;

/// <summary>
/// Tarea 239: una excepción que la red del hilo de la interfaz (<see cref="GuardedSynchronizationContext"/>,
/// <see cref="UiDeferral"/>) da por atendida no puede dejar una edición a medias. Los comandos ya son todo o nada
/// (instantáneas); lo que podía quedar a medias es la sesión: si un suscriptor del historial lanzaba con la edición ya
/// aplicada, el reproductor y las vistas se quedaban con el proyecto de antes.
/// </summary>
public class EditFailureCoherenceTests
{
    [Fact]
    public void SuscriptorDelHistorialLanza_LaEdicionQuedaEnteraYElReproductorAlDia() => UiThread.Run(() =>
    {
        var playback = new FakePlayback();
        var session = new EditorSession(playback, new FakeProber(), new FakeDialogs(), new SequentialIdGenerator());
        int edited = 0;
        bool fail = true;
        session.HistoryChanged += (_, _) =>
        {
            if (fail)
            {
                throw new InvalidOperationException("falla un panel");
            }
        };
        session.ProjectEdited += (_, _) => edited++;
        int handled = 0;
        var guard = new GuardedSynchronizationContext(new InlineContext(), _ => ++handled > 0);

        guard.Post(_ => session.Execute(new RenameProject("Nuevo nombre")), null);

        Assert.Equal(1, handled);
        Assert.Equal("Nuevo nombre", session.Project.Name);
        Assert.Equal("Nuevo nombre", Assert.Single(playback.Reloads).Name);
        Assert.Equal(1, edited);
        Assert.True(session.CanUndo);

        fail = false;
        Assert.True(session.Undo());
        Assert.NotEqual("Nuevo nombre", session.Project.Name);
        Assert.NotEqual("Nuevo nombre", playback.Reloads[^1].Name);
    });

    [Fact]
    public void UnSuscriptorDeLaEdicionLanza_LosDemasSeEnteran() => UiThread.Run(() =>
    {
        var session = new EditorSession(new FakePlayback(), new FakeProber(), new FakeDialogs(), new SequentialIdGenerator());
        int after = 0;
        session.ProjectEdited += (_, _) => throw new InvalidOperationException("falla la línea de tiempo");
        session.ProjectEdited += (_, _) => after++;

        Assert.Throws<InvalidOperationException>(() => session.Execute(new RenameProject("Otro")));

        Assert.Equal(1, after);
        Assert.Equal("Otro", session.Project.Name);
    });

    [Fact]
    public void ComandoQueFallaAlAplicar_NoCambiaNadaYNoRefrescaDeMas() => UiThread.Run(() =>
    {
        var playback = new FakePlayback();
        var session = new EditorSession(playback, new FakeProber(), new FakeDialogs(), new SequentialIdGenerator());
        int edited = 0;
        session.ProjectEdited += (_, _) => edited++;
        string name = session.Project.Name;

        Assert.Throws<InvalidOperationException>(() => session.Execute(new RenameProject(null)));

        Assert.Equal(name, session.Project.Name);
        Assert.Empty(playback.Reloads);
        Assert.Equal(0, edited);
        Assert.False(session.CanUndo);
    });

    private sealed class InlineContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => d(state);
    }

    private sealed class RenameProject(string? name) : ICommand
    {
        private string _before = "";

        public string Description => "Renombrar";

        public void Apply(Project project)
        {
            _before = project.Name;
            project.Name = name ?? throw new InvalidOperationException("comando roto");
        }

        public void Revert(Project project) => project.Name = _before;

        public bool TryMerge(ICommand following) => false;
    }
}
