using Rodaje.App.Services;
using Rodaje.App.Tests.Support;
using Rodaje.Core.Model;

namespace Rodaje.App.Tests.Session;

/// <summary>
/// Semillas 1006161 y 1016412 del fuzz largo: dentro de un compuesto anidado, deshacer la creación del compuesto de fuera
/// borraba esa secuencia de las migas de pan pero no de la ruta. La de dentro seguía existiendo, así que la sesión no subía,
/// y al volver («Salir del compuesto» o la miga) mostraba una secuencia que ya no estaba: KeyNotFoundException en
/// <see cref="EditorSession.Sequence"/> en la siguiente acción.
/// </summary>
public class CompoundNestedUndoTests
{
    [Fact]
    public void DeshacerElCompuestoDeFuera_DesdeUnoAnidado_SubeAUnaSecuenciaQueExiste() => UiThread.Run(async () =>
    {
        var playback = new FakePlayback();
        var prober = new FakeProber().Video("a.mp4", 10).Video("b.mp4", 10);
        var session = new EditorSession(playback, prober, new FakeDialogs(), new SequentialIdGenerator());
        await session.OpenFilesAsync([Path.GetFullPath("a.mp4"), Path.GetFullPath("b.mp4")]);
        string root = session.SequenceId;

        session.Select([session.MainTrack!.Clips[0].Id]);
        Assert.True(session.CreateCompoundFromSelection());
        session.SelectAll();
        Assert.True(session.CreateCompoundFromSelection());
        Assert.True(session.EnterCompound(session.MainTrack!.Clips.Single().Id));
        Assert.True(session.EnterCompound(session.MainTrack!.Clips.Single(c => c.IsCompound).Id));

        session.Undo();

        Assert.NotNull(session.Project.FindSequence(session.SequenceId));
        Assert.Equal(root, session.SequenceId);
        Assert.Equal(root, playback.Loads[^1].SequenceId);
        Assert.All(session.SequencePath, s => Assert.NotNull(session.Project.FindSequence(s.Id)));
        Assert.Single(session.SequencePath);
        Assert.False(session.LeaveCompound());
        Assert.Equal(2, session.MainTrack!.Clips.Count);
    });
}
