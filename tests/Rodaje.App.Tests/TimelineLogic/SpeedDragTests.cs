using System.Diagnostics;
using Rodaje.App.Services;
using Rodaje.App.Tests.Support;
using Rodaje.App.Views.Timeline;
using Rodaje.Core.Commands;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Xunit.Abstractions;

namespace Rodaje.App.Tests.TimelineLogic;

/// <summary>Alt + arrastrar un borde: estira cambiando la velocidad, en vivo y en un paso del historial.</summary>
public class SpeedDragTests(ITestOutputHelper output)
{
    private static Time S(double seconds) => Time.FromSeconds(seconds);

    /// <summary>Pista principal: a [0,10), b [10,20), c [20,25), con su audio enlazado; «muda» (8 s, sin audio) en la biblioteca.</summary>
    private static async Task<EditorSession> ThreeClips()
    {
        var prober = new FakeProber().Video("a.mp4", 10).Video("b.mp4", 10).Video("c.mp4", 5).Video("muda.mp4", 8, audio: false);
        var session = new EditorSession(new FakePlayback(), prober, new FakeDialogs(), new SequentialIdGenerator());
        await session.OpenFilesAsync([Path.GetFullPath("a.mp4"), Path.GetFullPath("b.mp4"), Path.GetFullPath("c.mp4")]);
        await session.ImportMediaAsync([Path.GetFullPath("muda.mp4")]);
        return session;
    }

    private static string Layout(EditorSession session, Track track) =>
        string.Join(" ", track.Clips.Select(c => FormattableString.Invariant($"{session.Project.GetMedia(c.MediaId).Name}@{c.Start.ToSeconds():0.##}-{c.End.ToSeconds():0.##}")));

    private static Clip ClipOf(EditorSession session, string name, int track = 0) =>
        session.Sequence.VideoTracks[track].Clips.First(c => session.Project.GetMedia(c.MediaId).Name == name);

    private static DragOptions Options(EditorSession session, bool magnetic = true, bool snapping = false) => new()
    {
        Snapping = snapping,
        MagneticTrackId = magnetic ? session.MainTrack!.Id : null,
        RippleScope = session.Timeline.RippleScope,
        PixelsPerSecond = 100,
    };

    private static ClipDragSession Start(EditorSession session, Clip clip, DragOperation operation, double grab, DragOptions options)
    {
        var drag = new ClipDragSession(session, new ClipDrag(session.Project, session.SequenceId, clip.Id, operation, S(grab), options, session.Position));
        drag.Begin();
        return drag;
    }

    private static void PutOnV2(EditorSession session, double at, double length)
    {
        string muda = session.Project.Media.First(m => m.Name == "muda").Id;
        Assert.True(session.Execute(Edits.Overwrite(session.SequenceId, muda, Time.Zero, S(length), session.Sequence.VideoTracks[1].Id, S(at))));
    }

    [Fact]
    public void EstirarElFinal_EnLaPrincipal_CambiaLaVelocidadCorreLoQueSigueYEsUnPaso() => UiThread.Run(async () =>
    {
        EditorSession session = await ThreeClips();
        int steps = session.History.Count;
        Clip a = ClipOf(session, "a");
        ClipDragSession drag = Start(session, a, DragOperation.StretchEnd, 10, Options(session));

        DragPlan plan = drag.Move(S(15), null);
        Assert.Equal("a@0-15 b@15-25 c@25-30", Layout(session, session.MainTrack!));
        Assert.Equal("a@0-15 b@15-25 c@25-30", Layout(session, session.Sequence.AudioTracks[0]));
        Assert.True(plan.IsLive);
        Assert.Equal(S(15), plan.Balloon!.Value.At);
        Assert.StartsWith("0,67× · ", plan.Balloon.Value.Text, StringComparison.Ordinal);

        drag.Move(S(30), null);
        drag.Move(S(20), null);
        drag.Complete();
        Assert.Equal("a@0-20 b@20-30 c@30-35", Layout(session, session.MainTrack!));
        Clip stretched = ClipOf(session, "a");
        Assert.Equal(new Rational(1, 2), stretched.Speed!.Rate);
        Assert.Equal((S(0), S(10)), (stretched.SourceIn, stretched.SourceOut));
        Assert.Equal(new Rational(1, 2), session.Sequence.AudioTracks[0].Clips[0].Speed!.Rate);
        Assert.Equal("0,5×", SpeedBadge.Text(stretched));
        Assert.Equal(steps + 1, session.History.Count);
        Assert.Equal("Cambiar velocidad", session.UndoDescription);

        Assert.True(session.Undo());
        Assert.Equal("a@0-10 b@10-20 c@20-25", Layout(session, session.MainTrack!));
        Assert.Null(ClipOf(session, "a").Speed);
    });

    [Fact]
    public void EstirarSinRipple_NoPasaAlVecinoYElInicioDejaQuietoElFinal() => UiThread.Run(async () =>
    {
        EditorSession session = await ThreeClips();
        PutOnV2(session, 2, 4);
        PutOnV2(session, 8, 2);
        Clip first = session.Sequence.VideoTracks[1].Clips[0];
        DragOptions free = Options(session, magnetic: false);
        int steps = session.History.Count;

        ClipDragSession drag = Start(session, first, DragOperation.StretchEnd, 6, free);
        drag.Move(S(7), null);
        drag.Move(S(12), null);
        drag.Complete();
        Assert.Equal("muda@2-8 muda@8-10", Layout(session, session.Sequence.VideoTracks[1]));
        Assert.Equal(new Rational(2, 3), session.Sequence.VideoTracks[1].Clips[0].Speed!.Rate);
        Assert.Equal("a@0-10 b@10-20 c@20-25", Layout(session, session.MainTrack!));
        Assert.Equal(steps + 1, session.History.Count);
        Assert.True(session.Undo());
        Assert.Equal("muda@2-6 muda@8-10", Layout(session, session.Sequence.VideoTracks[1]));
        Assert.Null(session.Sequence.VideoTracks[1].Clips[0].Speed);
        Assert.True(session.Redo());

        drag = Start(session, session.Sequence.VideoTracks[1].Clips[0], DragOperation.StretchStart, 2, free);
        drag.Move(S(-5), null);
        drag.Complete();
        Assert.Equal("muda@0-8 muda@8-10", Layout(session, session.Sequence.VideoTracks[1]));
        Assert.Equal(new Rational(1, 2), session.Sequence.VideoTracks[1].Clips[0].Speed!.Rate);
    });

    [Fact]
    public void LaVelocidadQuedaEntre01xY100x() => UiThread.Run(async () =>
    {
        EditorSession session = await ThreeClips();
        Clip c = ClipOf(session, "c");
        ClipDragSession drag = Start(session, c, DragOperation.StretchEnd, 25, Options(session));
        drag.Move(S(200), null);
        Assert.Equal(S(50), ClipOf(session, "c").Duration);
        Assert.Equal(new Rational(1, 10), ClipOf(session, "c").Speed!.Rate);

        drag.Move(S(-100), null);
        drag.Complete();
        Assert.Equal(S(0.05), ClipOf(session, "c").Duration);
        Assert.Equal(new Rational(100, 1), ClipOf(session, "c").Speed!.Rate);
    });

    [Fact]
    public void AltSobreElCuerpoSigueSiendoSlipYUnCongeladoSeRecorta() => UiThread.Run(async () =>
    {
        EditorSession session = await ThreeClips();
        SequenceEditCommand freeze = Edits.FreezeFrame(session.SequenceId, ClipOf(session, "b").Id, S(12), S(3));
        Assert.True(session.Execute(freeze));
        Clip frozen = session.Sequence.GetClip(freeze.CreatedIds[0]).Clip;
        Assert.Equal(S(3), frozen.Duration);

        ClipDragSession drag = Start(session, frozen, DragOperation.StretchEnd, 15, Options(session));
        DragPlan plan = drag.Move(S(16), null);
        drag.Cancel();
        Assert.Equal((S(12), S(16)), (plan.GhostStart, plan.GhostEnd));
        Assert.Null(plan.Balloon);

        var body = new ClipDrag(session.Project, session.SequenceId, frozen.Id, DragOperation.Slip, S(14), Options(session), null);
        Assert.Equal(DragOperation.Slip, body.Operation);
    });

    [Fact]
    [Trait("Categoria", "TiempoReal")]
    public void CadaPasoDeEstirarTardaMenosDe16ms() => UiThread.Run(async () =>
    {
        EditorSession session = await ThreeClips();
        string muda = session.Project.Media.First(m => m.Name == "muda").Id;
        for (int i = 0; i < 200; i++)
        {
            Assert.True(session.Execute(Edits.AppendMedia(session.SequenceId, muda, Time.Zero, S(2), session.MainTrack!.Id)));
        }

        ClipDragSession drag = Start(session, ClipOf(session, "a"), DragOperation.StretchEnd, 10, Options(session));
        var times = new List<double>();
        for (int i = 0; i < 120; i++)
        {
            long start = Stopwatch.GetTimestamp();
            drag.Move(S(10 + ((i % 40) * 0.25)), null);
            times.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }

        drag.Complete();
        times.Sort();
        output.WriteLine($"Estirar con 203 clips: mediana {times[times.Count / 2]:0.000} ms, p95 {times[times.Count * 95 / 100]:0.000} ms");
        Assert.True(times[times.Count * 95 / 100] < 16, $"p95 {times[times.Count * 95 / 100]:0.00} ms");
    });
}
