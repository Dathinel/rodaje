using Rodaje.App.Services;
using Rodaje.App.Tests.Support;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Tests.Session;

public class ThreePointSessionTests
{
    private static Time S(double seconds) => Time.FromSeconds(seconds);

    private static async Task<(EditorSession Session, FakePlayback Playback, string Media)> TwoClips()
    {
        var playback = new FakePlayback();
        var prober = new FakeProber().Video("a.mp4", 10).Video("b.mp4", 10).Video("c.mp4", 30);
        var session = new EditorSession(playback, prober, new FakeDialogs(), new SequentialIdGenerator());
        await session.OpenFilesAsync([Path.GetFullPath("a.mp4"), Path.GetFullPath("b.mp4")]);
        await session.ImportMediaAsync([Path.GetFullPath("c.mp4")]);
        return (session, playback, session.Project.Media.Single(m => m.Name.StartsWith('c')).Id);
    }

    private static void At(EditorSession session, double seconds) =>
        session.UpdateTransport(PlaybackSnapshot.Empty with { Position = S(seconds) });

    [Fact]
    public void Origen_MarcasYSobrescribir_EnElCursorDelPrograma() => UiThread.Run(async () =>
    {
        (EditorSession session, FakePlayback playback, string media) = await TwoClips();
        At(session, 4);
        Assert.True(session.OpenSource(media));
        Assert.Equal(EditorSession.SourceSequenceId, playback.Loads[^1].SequenceId);
        Assert.Equal(S(4), session.ProgramPosition);

        At(session, 10);
        session.MarkIn();
        At(session, 12);
        session.MarkOut();

        Assert.Equal(ThreePointOutcome.Done, session.ThreePointEdit(PlacementMode.Overwrite));
        Clip placed = session.MainTrack!.Clips.Single(c => c.MediaId == media);
        Assert.Equal(S(4), placed.Start);
        Assert.Equal(S(10), placed.SourceIn);
        Assert.Equal(S(12) + Time.FromFrames(1, Rational.Fps30), placed.SourceOut);
        Assert.Equal("Sobrescribir", session.UndoDescription);
        Assert.Equal(S(20), session.MainTrack.End);
    });

    [Fact]
    public void CuatroPuntosDistintos_PideDecidirYLuegoAjustaLaVelocidad() => UiThread.Run(async () =>
    {
        (EditorSession session, _, string media) = await TwoClips();
        At(session, 2);
        session.MarkIn();
        At(session, 3);
        session.MarkOut();
        session.OpenSource(media);
        At(session, 0);
        session.MarkIn();
        At(session, 1.9);
        session.MarkOut();
        int steps = session.History.Count;

        Assert.Equal(ThreePointOutcome.NeedsDecision, session.ThreePointEdit(PlacementMode.Overwrite));
        Assert.Equal(steps, session.History.Count);
        Assert.Equal(ThreePointOutcome.Done, session.ThreePointEdit(PlacementMode.Overwrite, FourPointFit.FitToFill));

        Clip placed = session.MainTrack!.Clips.Single(c => c.MediaId == media);
        Assert.Equal((S(2), S(3) + Time.FromFrames(1, Rational.Fps30)), (placed.Start, placed.End));
        Assert.NotNull(placed.Speed);
        Assert.Null(session.SequenceIn);
    });

    [Fact]
    public void LaPrincipalMagnetica_NoDejaHuecoAlSobrescribirDespuesDelFinal() => UiThread.Run(async () =>
    {
        (EditorSession session, _, string media) = await TwoClips();
        At(session, 0);
        session.OpenSource(media);
        session.MarkOut();
        session.ShowProgram();
        At(session, 0);
        At(session, 25);

        Assert.True(session.Timeline.MagneticMainTrack);
        Assert.Equal(ThreePointOutcome.Done, session.ThreePointEdit(PlacementMode.Overwrite));
        Assert.Equal(S(20), session.MainTrack!.Clips[^1].Start);
    });

    [Fact]
    public void SinOrigen_AvisaYNoEdita() => UiThread.Run(async () =>
    {
        (EditorSession session, _, _) = await TwoClips();
        int steps = session.History.Count;

        Assert.Equal(ThreePointOutcome.Failed, session.ThreePointEdit(PlacementMode.Insert));
        Assert.Equal(steps, session.History.Count);
    });
}
