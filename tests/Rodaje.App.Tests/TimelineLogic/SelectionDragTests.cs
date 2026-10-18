using Rodaje.App.Services;
using Rodaje.App.Tests.Support;
using Rodaje.App.Views.Timeline;
using Rodaje.Core.Commands;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Tests.TimelineLogic;

/// <summary>Arrastrar uno de varios clips seleccionados mueve a todos con el mismo desplazamiento, en un paso.</summary>
public class SelectionDragTests
{
    private static Time S(double seconds) => Time.FromSeconds(seconds);

    /// <summary>Principal: a [0,10), b [10,20), c [20,25) con su audio; en V2, «muda» [30,34).</summary>
    private static async Task<EditorSession> Timeline()
    {
        var prober = new FakeProber().Video("a.mp4", 10).Video("b.mp4", 10).Video("c.mp4", 5).Video("muda.mp4", 8, audio: false);
        var session = new EditorSession(new FakePlayback(), prober, new FakeDialogs(), new SequentialIdGenerator());
        await session.OpenFilesAsync([Path.GetFullPath("a.mp4"), Path.GetFullPath("b.mp4"), Path.GetFullPath("c.mp4")]);
        await session.ImportMediaAsync([Path.GetFullPath("muda.mp4")]);
        string muted = session.Project.Media.First(m => m.Name == "muda").Id;
        Assert.True(session.Execute(Edits.Overwrite(session.SequenceId, muted, Time.Zero, S(4), session.Sequence.VideoTracks[1].Id, S(30))));
        return session;
    }

    private static string Layout(EditorSession session, Track track) =>
        string.Join(" ", track.Clips.Select(c => FormattableString.Invariant($"{session.Project.GetMedia(c.MediaId).Name}@{c.Start.ToSeconds():0.##}-{c.End.ToSeconds():0.##}")));

    private static string All(EditorSession session) => string.Join(" | ", session.Sequence.AllTracks.Select(t => Layout(session, t)));

    private static Clip Named(EditorSession session, string name) =>
        session.Sequence.VideoTracks.SelectMany(t => t.Clips).First(c => session.Project.GetMedia(c.MediaId).Name == name);

    private static DragOptions Options(EditorSession session, bool magnetic, bool snapping = false) => new()
    {
        Snapping = snapping,
        MagneticTrackId = magnetic ? session.MainTrack!.Id : null,
        RippleScope = session.Timeline.RippleScope,
        PixelsPerSecond = 100,
    };

    private static TrackRow Row(EditorSession session, Track track) =>
        TrackLayout.Build(session.Sequence, session.MainTrack).Find(track.Id)!;

    private static ClipDragSession Start(EditorSession session, Clip clip, double grab, DragOptions options)
    {
        var drag = new ClipDragSession(session, new ClipDrag(session.Project, session.SequenceId, clip.Id, DragOperation.Move, S(grab), options, null,
            selection: session.SelectedClipIds));
        drag.Begin();
        return drag;
    }

    [Fact]
    public void ArrastrarUnoMueveATodaLaSeleccion_EnUnPaso() => UiThread.Run(async () =>
    {
        EditorSession session = await Timeline();
        string before = All(session);
        int steps = session.History.Count;
        Clip c = Named(session, "c");
        session.Select([c.Id, Named(session, "muda").Id]);

        ClipDragSession drag = Start(session, c, 22, Options(session, magnetic: false));
        drag.Move(S(25), Row(session, session.MainTrack!));
        drag.Move(S(27), Row(session, session.MainTrack!));
        drag.Complete();

        Assert.Equal("c@25-30", Layout(session, session.MainTrack!).Split(' ')[^1]);
        Assert.Equal("muda@35-39", Layout(session, session.Sequence.VideoTracks[1]));
        Assert.Equal("a@0-10 b@10-20 c@25-30", Layout(session, session.Sequence.AudioTracks[0]));
        Assert.Equal(steps + 1, session.History.Count);
        session.Undo();
        Assert.Equal(before, All(session));
    });

    [Fact]
    public void SiUnoChocaNoSeMueveNinguno_YSinPistaParaTodosSoloCambiaElTiempo() => UiThread.Run(async () =>
    {
        EditorSession session = await Timeline();
        string before = All(session);
        Clip b = Named(session, "b");
        Clip muda = Named(session, "muda");
        session.Select([b.Id, muda.Id]);

        ClipDragSession blocked = Start(session, b, 12, Options(session, magnetic: false));
        blocked.Move(S(15), Row(session, session.MainTrack!));
        blocked.Complete();
        Assert.Equal(before, All(session));
        Assert.Equal(0, blocked.LiveSteps);

        // Bajar «muda» a V1 llevaría «b» por debajo de V1, que no existe: se mueven solo en el tiempo.
        session.Select([Named(session, "c").Id, muda.Id]);
        ClipDragSession down = Start(session, muda, 31, Options(session, magnetic: false));
        down.Move(S(33), Row(session, session.MainTrack!));
        down.Complete();
        Assert.Equal("muda@32-36", Layout(session, session.Sequence.VideoTracks[1]));
        Assert.Equal("a@0-10 b@10-20 c@22-27", Layout(session, session.MainTrack!));
    });

    [Fact]
    public void ConLaPrincipalMagnetica_LosSeleccionadosSeReordenanJuntos() => UiThread.Run(async () =>
    {
        EditorSession session = await Timeline();
        string before = All(session);
        Clip a = Named(session, "a");
        session.Select([a.Id, Named(session, "c").Id]);

        ClipDragSession drag = Start(session, a, 5, Options(session, magnetic: true));
        DragPlan plan = drag.Move(S(30), Row(session, session.MainTrack!));
        drag.Complete();

        Assert.Equal("b@0-10 a@10-20 c@20-25", Layout(session, session.MainTrack!));
        Assert.Equal("b@0-10 a@10-20 c@20-25", Layout(session, session.Sequence.AudioTracks[0]));
        Assert.Equal(S(10), plan.InsertAt);
        Assert.Equal("Mover clip", session.UndoDescription);
        session.Undo();
        Assert.Equal(before, All(session));
    });

    [Fact]
    public void ElImanCorrigeAlGrupoEntero() => UiThread.Run(async () =>
    {
        EditorSession session = await Timeline();
        Clip c = Named(session, "c");
        session.Select([c.Id, Named(session, "muda").Id]);

        ClipDragSession drag = Start(session, c, 22, Options(session, magnetic: false, snapping: true));
        DragPlan plan = drag.Move(S(22.06), Row(session, session.MainTrack!));
        drag.Complete();

        Assert.Equal(S(20), plan.SnapGuide);
        Assert.Equal("muda@30-34", Layout(session, session.Sequence.VideoTracks[1]));
        Assert.Equal("a@0-10 b@10-20 c@20-25", Layout(session, session.MainTrack!));
    });
}
