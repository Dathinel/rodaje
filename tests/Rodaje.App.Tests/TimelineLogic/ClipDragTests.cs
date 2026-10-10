using Rodaje.App.Services;
using Rodaje.App.Tests.Support;
using Rodaje.App.Views.Timeline;
using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Tests.TimelineLogic;

/// <summary>Arrastres completos contra la sesión: comando, vivo o al soltar, un paso del historial y Esc.</summary>
public class ClipDragTests
{
    private static Time S(double seconds) => Time.FromSeconds(seconds);

    /// <summary>Pista principal: a [0,10), b [10,20), c [20,25), con su audio enlazado en «Audio 1».</summary>
    private static async Task<EditorSession> ThreeClips()
    {
        var prober = new FakeProber().Video("a.mp4", 10).Video("b.mp4", 10).Video("c.mp4", 5).Audio("m.mp3", 30).Video("muda.mp4", 8, audio: false);
        var session = new EditorSession(new FakePlayback(), prober, new FakeDialogs(), new SequentialIdGenerator());
        await session.OpenFilesAsync([Path.GetFullPath("a.mp4"), Path.GetFullPath("b.mp4"), Path.GetFullPath("c.mp4")]);
        await session.ImportMediaAsync([Path.GetFullPath("muda.mp4")]);
        return session;
    }

    private static string MainLayout(EditorSession session) => Layout(session, session.MainTrack!);

    private static string Layout(EditorSession session, Track track) =>
        string.Join(" ", track.Clips.Select(c => FormattableString.Invariant($"{session.Project.GetMedia(c.MediaId).Name}@{c.Start.ToSeconds():0.##}-{c.End.ToSeconds():0.##}")));

    private static Clip ClipOf(EditorSession session, string name, int track = 0) =>
        session.Sequence.VideoTracks[track].Clips.First(c => session.Project.GetMedia(c.MediaId).Name == name);

    private static string Muted(EditorSession session) => session.Project.Media.First(m => m.Name == "muda").Id;

    private static DragOptions Options(EditorSession session, bool snapping = false, bool magnetic = true, bool autoRipple = false, bool ignoreGroups = false) => new()
    {
        Snapping = snapping,
        MagneticTrackId = magnetic ? session.MainTrack!.Id : null,
        AutoRipple = autoRipple,
        RippleScope = session.Timeline.RippleScope,
        IgnoreGroups = ignoreGroups,
        PixelsPerSecond = 100,
    };

    private static TrackRow Row(EditorSession session, Track track) =>
        TrackLayout.Build(session.Sequence, session.MainTrack).Find(track.Id)!;

    private static ClipDragSession Start(EditorSession session, Clip clip, DragOperation operation, double grab, DragOptions options)
    {
        var drag = new ClipDragSession(session, new ClipDrag(session.Project, session.SequenceId, clip.Id, operation, S(grab), options, session.Position));
        drag.Begin();
        return drag;
    }

    private static void PutOnV2(EditorSession session, double at, double length = 4) =>
        Assert.True(session.Execute(Edits.Overwrite(session.SequenceId, Muted(session), Time.Zero, S(length), session.Sequence.VideoTracks[1].Id, S(at))));

    [Fact]
    public void ReordenarEnLaPrincipal_EsEnVivoYUnSoloPaso() => UiThread.Run(async () =>
    {
        EditorSession session = await ThreeClips();
        int steps = session.History.Count;
        Clip c = ClipOf(session, "c");
        ClipDragSession drag = Start(session, c, DragOperation.Move, 22, Options(session));
        TrackRow main = Row(session, session.MainTrack!);

        DragPlan plan = drag.Move(S(12), main);
        Assert.Equal("a@0-10 c@10-15 b@15-25", MainLayout(session));
        Assert.Equal(S(10), plan.InsertAt);
        Assert.True(plan.IsLive);

        drag.Move(S(1), main);
        Assert.Equal("c@0-5 a@5-15 b@15-25", MainLayout(session));
        Assert.Equal("c@0-5 a@5-15 b@15-25", Layout(session, session.Sequence.AudioTracks[0]));
        drag.Move(S(1.5), main);
        drag.Complete();

        Assert.Equal(steps + 1, session.History.Count);
        Assert.Equal("Mover clip", session.UndoDescription);
        Assert.Equal(2, drag.LiveSteps);
        session.Undo();
        Assert.Equal("a@0-10 b@10-20 c@20-25", MainLayout(session));
        Assert.Equal("a@0-10 b@10-20 c@20-25", Layout(session, session.Sequence.AudioTracks[0]));
    });

    [Fact]
    public void Esc_CancelaElGestoEntero() => UiThread.Run(async () =>
    {
        EditorSession session = await ThreeClips();
        int steps = session.History.Count;
        ClipDragSession drag = Start(session, ClipOf(session, "a"), DragOperation.Move, 5, Options(session));
        drag.Move(S(23), Row(session, session.MainTrack!));
        Assert.Equal("b@0-10 c@10-15 a@15-25", MainLayout(session));

        drag.Cancel();

        Assert.Equal("a@0-10 b@10-20 c@20-25", MainLayout(session));
        Assert.Equal(steps, session.History.Count);
        Assert.False(session.IsInGesture);
    });

    [Fact]
    public void MoverEnPistaLibre_SePegaYNoPisaOtrosClips() => UiThread.Run(async () =>
    {
        EditorSession session = await ThreeClips();
        PutOnV2(session, 2);
        PutOnV2(session, 12);
        Track v2 = session.Sequence.VideoTracks[1];
        Clip first = v2.Clips[0];
        int steps = session.History.Count;
        ClipDragSession drag = Start(session, first, DragOperation.Move, 3, Options(session, snapping: true));
        TrackRow row = Row(session, v2);

        // Sin nada a menos de 8 px (0,08 s), queda donde se suelta.
        drag.Move(S(4.0), row);
        Assert.Equal("muda@3-7 muda@12-16", Layout(session, v2));

        // El final del clip queda a 0,03 s del inicio de «b» (10 s) y se pega ahí.
        DragPlan plan = drag.Move(S(7.04), row);
        Assert.Equal(S(10), plan.SnapGuide);
        Assert.Equal("muda@6-10 muda@12-16", Layout(session, v2));

        // Encima del otro clip: no se puede, queda donde estaba válido.
        drag.Move(S(11), row);
        Assert.Equal("muda@6-10 muda@12-16", Layout(session, v2));

        drag.Complete();
        Assert.Equal(steps + 1, session.History.Count);
        session.Undo();
        Assert.Equal("muda@2-6 muda@12-16", Layout(session, v2));
    });

    [Fact]
    public void SacarDeLaPrincipal_SeAplicaAlSoltarYCierraElHueco() => UiThread.Run(async () =>
    {
        EditorSession session = await ThreeClips();
        Track v2 = session.Sequence.VideoTracks[1];
        ClipDragSession drag = Start(session, ClipOf(session, "b"), DragOperation.Move, 12, Options(session));

        DragPlan plan = drag.Move(S(32), Row(session, v2));
        Assert.False(plan.IsLive);
        Assert.Equal(v2.Id, plan.GhostTrackId);
        Assert.Equal(S(30), plan.GhostStart);
        Assert.Equal("a@0-10 b@10-20 c@20-25", MainLayout(session));

        drag.Complete();

        Assert.Equal("a@0-10 c@10-15", MainLayout(session));
        Assert.Equal("b@30-40", Layout(session, v2));
        Assert.Equal("a@0-10 c@10-15 b@30-40", Layout(session, session.Sequence.AudioTracks[0]));
        session.Undo();
        Assert.Equal("a@0-10 b@10-20 c@20-25", MainLayout(session));
    });

    [Fact]
    public void EntrarALaPrincipal_InsertaEnElCorteMasCercano() => UiThread.Run(async () =>
    {
        EditorSession session = await ThreeClips();
        PutOnV2(session, 40);
        Track v2 = session.Sequence.VideoTracks[1];
        ClipDragSession drag = Start(session, v2.Clips[0], DragOperation.Move, 41, Options(session));

        DragPlan plan = drag.Move(S(11.5), Row(session, session.MainTrack!));
        Assert.Equal(S(10), plan.InsertAt);
        Assert.False(plan.IsLive);
        drag.Complete();

        Assert.Equal("a@0-10 muda@10-14 b@14-24 c@24-29", MainLayout(session));
        Assert.Equal("a@0-10 b@14-24 c@24-29", Layout(session, session.Sequence.AudioTracks[0]));
        Assert.Empty(v2.Clips);
    });

    [Fact]
    public void ConLaPrincipalApagada_SeMueveComoPistaLibre() => UiThread.Run(async () =>
    {
        EditorSession session = await ThreeClips();
        ClipDragSession drag = Start(session, ClipOf(session, "c"), DragOperation.Move, 21, Options(session, magnetic: false));
        drag.Move(S(31), Row(session, session.MainTrack!));
        drag.Complete();
        Assert.Equal("a@0-10 b@10-20 c@30-35", MainLayout(session));
        Assert.Equal("a@0-10 b@10-20 c@30-35", Layout(session, session.Sequence.AudioTracks[0]));
    });

    [Fact]
    public void RecortarEnPistaLibre_SeDetieneEnElVecino() => UiThread.Run(async () =>
    {
        EditorSession session = await ThreeClips();
        PutOnV2(session, 0, 4);
        PutOnV2(session, 6, 4);
        Track v2 = session.Sequence.VideoTracks[1];
        ClipDragSession drag = Start(session, v2.Clips[0], DragOperation.TrimEnd, 4, Options(session));
        DragPlan plan = drag.Move(S(9), Row(session, v2));
        Assert.Equal(S(6), plan.GhostEnd);
        drag.Complete();
        Assert.Equal("muda@0-6 muda@6-10", Layout(session, v2));

        drag = Start(session, v2.Clips[1], DragOperation.TrimStart, 6, Options(session));
        drag.Move(S(7.5), Row(session, v2));
        drag.Complete();
        Assert.Equal("muda@0-6 muda@7.5-10", Layout(session, v2));
        Assert.Equal(S(1.5), v2.Clips[1].SourceIn);
    });

    [Fact]
    public void RecortarEnLaPrincipal_CorreLoQueSigue() => UiThread.Run(async () =>
    {
        EditorSession session = await ThreeClips();
        ClipDragSession drag = Start(session, ClipOf(session, "a"), DragOperation.TrimEnd, 10, Options(session));
        drag.Move(S(7), Row(session, session.MainTrack!));
        drag.Move(S(6), Row(session, session.MainTrack!));
        drag.Complete();
        Assert.Equal("a@0-6 b@6-16 c@16-21", MainLayout(session));
        Assert.Equal("a@0-6 b@6-16 c@16-21", Layout(session, session.Sequence.AudioTracks[0]));
        Assert.Equal("Recortar final", session.UndoDescription);

        // El inicio con ripple: el clip no se mueve, lo que se quita del principio se recupera al final.
        drag = Start(session, ClipOf(session, "b"), DragOperation.TrimStart, 6, Options(session));
        drag.Move(S(8), Row(session, session.MainTrack!));
        drag.Complete();
        Assert.Equal("a@0-6 b@6-14 c@14-19", MainLayout(session));
        Assert.Equal(S(2), ClipOf(session, "b").SourceIn);
    });

    [Fact]
    public void RecortarNoPasaElMedioNiDejaElClipVacio() => UiThread.Run(async () =>
    {
        EditorSession session = await ThreeClips();
        ClipDragSession drag = Start(session, ClipOf(session, "c"), DragOperation.TrimEnd, 25, Options(session));
        drag.Move(S(40), Row(session, session.MainTrack!));
        Assert.Equal("a@0-10 b@10-20 c@20-25", MainLayout(session));
        drag.Move(S(0), Row(session, session.MainTrack!));
        drag.Complete();
        Assert.Equal(Time.FromFrames(1, session.Sequence.FrameRate), ClipOf(session, "c").Duration);
    });

    [Fact]
    public void Slip_MuestraOtraParteDelOrigenDentroDelMedio() => UiThread.Run(async () =>
    {
        EditorSession session = await ThreeClips();
        session.Execute(Edits.Trim(session.SequenceId, ClipOf(session, "a").Id, TrimEdge.In, S(3)));
        session.Execute(Edits.Trim(session.SequenceId, ClipOf(session, "a").Id, TrimEdge.Out, S(-3)));
        Clip a = ClipOf(session, "a");
        Assert.Equal((S(3), S(7)), (a.SourceIn, a.SourceOut));
        ClipDragSession drag = Start(session, a, DragOperation.Slip, 5, Options(session));

        drag.Move(S(4), Row(session, session.MainTrack!));
        Assert.Equal(S(4), ClipOf(session, "a").SourceIn);
        drag.Move(S(10), Row(session, session.MainTrack!));
        Assert.Equal(Time.Zero, ClipOf(session, "a").SourceIn);
        drag.Move(S(-20), Row(session, session.MainTrack!));
        Assert.Equal(S(6), ClipOf(session, "a").SourceIn);
        Assert.Equal(S(6), session.Sequence.AudioTracks[0].Clips[0].SourceIn);
        Assert.Equal(S(3), ClipOf(session, "a").Start);
        drag.Complete();
        Assert.Equal("Slip", session.UndoDescription);
    });

    [Fact]
    public void IgnorarGrupos_EditaSoloElClipAgarrado() => UiThread.Run(async () =>
    {
        EditorSession session = await ThreeClips();
        Clip c = ClipOf(session, "c");
        ClipDragSession drag = Start(session, c, DragOperation.TrimEnd, 25, Options(session, ignoreGroups: true));
        drag.Move(S(23), Row(session, session.MainTrack!));
        drag.Complete();

        Assert.Equal("a@0-10 b@10-20 c@20-23", MainLayout(session));
        Assert.Equal("a@0-10 b@10-20 c@20-25", Layout(session, session.Sequence.AudioTracks[0]));
        Assert.Equal(c.LinkId, session.Sequence.AudioTracks[0].Clips[2].LinkId);
        Assert.NotNull(c.LinkId);
    });

    [Fact]
    public void LaRanuraMagneticaVaAntesDelPrimerCentroADerecha()
    {
        Clip C(double length) => new() { Id = "x", MediaId = "m", SourceOut = S(length) };
        List<Clip> others = [C(10), C(4), C(6)];

        Assert.Equal((0, S(2)), ClipDrag.MagneticSlot(others, S(2), S(0), S(2)));
        Assert.Equal((1, S(12)), ClipDrag.MagneticSlot(others, S(2), S(7), S(2)));
        Assert.Equal((2, S(16)), ClipDrag.MagneticSlot(others, S(2), S(14), S(2)));
        Assert.Equal((3, S(22)), ClipDrag.MagneticSlot(others, S(2), S(60), S(2)));
        Assert.Equal((0, Time.Zero), ClipDrag.MagneticSlot([], Time.Zero, S(5), S(2)));
    }
}
