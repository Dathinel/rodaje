using Rodaje.App.Services;
using Rodaje.App.Tests.Support;
using Rodaje.Core.Model;

namespace Rodaje.App.Tests.Session;

public class CompoundSessionTests
{
    private static async Task<(EditorSession Session, FakePlayback Playback)> TwoClips()
    {
        var playback = new FakePlayback();
        var prober = new FakeProber().Video("a.mp4", 10).Video("b.mp4", 10);
        var session = new EditorSession(playback, prober, new FakeDialogs(), new SequentialIdGenerator());
        await session.OpenFilesAsync([Path.GetFullPath("a.mp4"), Path.GetFullPath("b.mp4")]);
        return (session, playback);
    }

    [Fact]
    public void Crear_EntrarYVolver_ConMigasDePanYUnPasoDelHistorial() => UiThread.Run(async () =>
    {
        (EditorSession session, FakePlayback playback) = await TwoClips();
        string root = session.SequenceId;
        session.SelectAll();
        int steps = session.History.Count;

        Assert.True(session.CreateCompoundFromSelection());

        Assert.Equal(steps + 1, session.History.Count);
        Clip compound = session.MainTrack!.Clips.Single();
        Assert.True(compound.IsCompound);
        Assert.Equal(compound.Id, session.SelectedCompoundId);

        Assert.True(session.EnterCompound(compound.Id));
        Assert.Equal(compound.SequenceId, session.SequenceId);
        Assert.Equal((compound.SequenceId!, compound.SequenceId!), (playback.Loads[^1].SequenceId, session.SequencePath[^1].Id));
        Assert.Equal(2, session.SequencePath.Count);
        Assert.Equal(2, session.MainTrack!.Clips.Count);

        Assert.True(session.LeaveCompound(0));
        Assert.Equal(root, session.SequenceId);
        Assert.Single(session.SequencePath);
    });

    [Fact]
    public void DeshacerLaCreacionDesdeDentro_VuelveALaSecuenciaQueQueda() => UiThread.Run(async () =>
    {
        (EditorSession session, FakePlayback playback) = await TwoClips();
        string root = session.SequenceId;
        session.SelectAll();
        session.CreateCompoundFromSelection();
        session.EnterCompound(session.MainTrack!.Clips[0].Id);

        session.Undo();

        Assert.Equal(root, session.SequenceId);
        Assert.Equal(root, playback.Loads[^1].SequenceId);
        Assert.Equal(2, session.MainTrack!.Clips.Count);
        Assert.Single(session.SequencePath);
    });

    [Fact]
    public void Descomponer_DevuelveLosClipsSeleccionados() => UiThread.Run(async () =>
    {
        (EditorSession session, _) = await TwoClips();
        session.SelectAll();
        List<string> before = [.. session.SelectedClipIds.Order()];
        session.CreateCompoundFromSelection();

        Assert.True(session.DecomposeCompound());

        Assert.Equal(before, session.SelectedClipIds.Order());
        Assert.Single(session.Project.Sequences);
    });
}
