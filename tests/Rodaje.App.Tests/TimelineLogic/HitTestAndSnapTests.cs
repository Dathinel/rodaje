using Rodaje.App.Views.Timeline;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Tests.TimelineLogic;

public class HitTestAndSnapTests
{
    private static Time S(double seconds) => Time.FromSeconds(seconds);

    private static Clip C(string id, double start, double length) =>
        new() { Id = id, MediaId = "m", SourceIn = Time.Zero, SourceOut = S(length), Start = S(start) };

    private static (Sequence Sequence, TrackLayout Layout, TimelineViewport Viewport) Fixture()
    {
        var v1 = new Track { Id = "v1", Name = "Video 1", Kind = TrackKind.Video, Clips = [C("a", 0, 10), C("b", 10, 5), C("tiny", 20, 0.1)] };
        var v2 = new Track { Id = "v2", Name = "Video 2", Kind = TrackKind.Video, Clips = [C("o", 3, 4)] };
        var a1 = new Track { Id = "a1", Name = "Audio 1", Kind = TrackKind.Audio, Clips = [C("s", 12, 6)] };
        var sequence = new Sequence { Id = "s", VideoTracks = [v1, v2], AudioTracks = [a1] };
        sequence.Markers.Add(new Marker { Id = "m1", Time = S(30) });
        sequence.Regions.Add(new Region { Id = "r1", Start = S(40), End = S(45) });
        return (sequence, TrackLayout.Build(sequence, v1), new TimelineViewport { PixelsPerSecond = 100, Width = 2000 });
    }

    private static double Mid(TrackRow row) => row.Top + (row.Height / 2);

    [Fact]
    public void ElPunteroCaeEnCuerpoBordesPistaONada()
    {
        (Sequence sequence, TrackLayout layout, TimelineViewport vp) = Fixture();
        TrackRow main = layout.Find("v1")!;

        TimelineHit body = TimelineHitTest.Hit(layout, vp, 500, Mid(main));
        Assert.Equal((HitKind.ClipBody, "a"), (body.Kind, body.Clip!.Id));
        Assert.Equal(S(5), body.Time);

        Assert.Equal(HitKind.ClipStart, TimelineHitTest.Hit(layout, vp, 1003, Mid(main)).Kind);
        Assert.Equal("b", TimelineHitTest.Hit(layout, vp, 1003, Mid(main)).Clip!.Id);
        Assert.Equal(HitKind.ClipEnd, TimelineHitTest.Hit(layout, vp, 997, Mid(main)).Kind);
        Assert.Equal(HitKind.Lane, TimelineHitTest.Hit(layout, vp, 1700, Mid(main)).Kind);
        Assert.Equal(HitKind.None, TimelineHitTest.Hit(layout, vp, 500, -20).Kind);
        Assert.Same(sequence.VideoTracks[0], body.Row!.Track);
    }

    [Fact]
    public void EnUnClipAngostoLosBordesDejanCuerpo()
    {
        (_, TrackLayout layout, TimelineViewport vp) = Fixture();
        TrackRow main = layout.Find("v1")!;

        // «tiny» mide 10 px: cada borde agarra 2,5 px.
        Assert.Equal(HitKind.ClipStart, TimelineHitTest.Hit(layout, vp, 2001, Mid(main)).Kind);
        Assert.Equal(HitKind.ClipBody, TimelineHitTest.Hit(layout, vp, 2005, Mid(main)).Kind);
        Assert.Equal(HitKind.ClipEnd, TimelineHitTest.Hit(layout, vp, 2009, Mid(main)).Kind);
    }

    [Fact]
    public void ElDesplazamientoCuentaAlBuscar()
    {
        (_, TrackLayout layout, TimelineViewport vp) = Fixture();
        vp.ScrollX = 1000;
        TimelineHit hit = TimelineHitTest.Hit(layout, vp, 100, Mid(layout.Find("v1")!));
        Assert.Equal(("b", S(11)), (hit.Clip!.Id, hit.Time));
    }

    [Fact]
    public void BusquedaBinariaDeLosVisibles()
    {
        (Sequence sequence, _, TimelineViewport vp) = Fixture();
        List<Clip> clips = sequence.VideoTracks[0].Clips;
        Assert.Equal(0, TimelineHitTest.FirstEndingAfter(clips, Time.Zero));
        Assert.Equal(1, TimelineHitTest.FirstEndingAfter(clips, S(10)));
        Assert.Equal(2, TimelineHitTest.FirstEndingAfter(clips, S(15)));
        Assert.Equal(3, TimelineHitTest.FirstEndingAfter(clips, S(99)));
        Assert.Equal(-1, TimelineHitTest.FindClipIndex(clips, vp, 1800));
        Assert.Equal(1, TimelineHitTest.FindClipIndex(clips, vp, 1000));
    }

    [Fact]
    public void LaSeleccionPorRectanguloTomaLoQueToca()
    {
        (_, TrackLayout layout, TimelineViewport vp) = Fixture();
        TrackRow v2 = layout.Find("v2")!;
        TrackRow main = layout.Find("v1")!;

        Assert.Equal(["o", "a", "b"], TimelineHitTest.Marquee(layout, vp, 1100, Mid(v2), 450, Mid(main)));
        Assert.Empty(TimelineHitTest.Marquee(layout, vp, 1600, Mid(v2), 1900, Mid(v2)));
    }

    [Fact]
    public void ElAjusteBuscaElPuntoMasCercanoDentroDelUmbral()
    {
        (Sequence sequence, _, _) = Fixture();
        SnapPoints snaps = SnapPoints.Collect(sequence, new HashSet<string> { "b" }, S(25));

        Assert.Equal(S(10), snaps.Nearest(S(10.05), S(0.08)));
        Assert.Null(snaps.Nearest(S(10.2), S(0.08)));
        Assert.Equal(S(25), snaps.Nearest(S(24.95), S(0.08)));
        Assert.Equal(S(30), snaps.Nearest(S(30.01), S(0.08)));
        Assert.Equal(S(45), snaps.Nearest(S(44.97), S(0.08)));
        Assert.Equal(Time.Zero, snaps.Nearest(S(0.01), S(0.08)));

        // Los bordes de «b» (10 y 15) no cuentan: 15 no está, 10 sí por ser el final de «a».
        Assert.Null(snaps.Nearest(S(15.01), S(0.08)));
    }

    [Fact]
    public void UnTramoSePegaPorElBordeQueMenosSeCorrige()
    {
        (Sequence sequence, _, _) = Fixture();
        SnapPoints snaps = SnapPoints.Collect(sequence, new HashSet<string>(), null);

        (Time correction, Time? guide) = snaps.SnapSpan(S(9.95), S(14.98), S(0.1));
        Assert.Equal(S(0.02), correction);
        Assert.Equal(S(15), guide);

        (correction, guide) = snaps.SnapSpan(S(50), S(55), S(0.1));
        Assert.Equal(Time.Zero, correction);
        Assert.Null(guide);
        Assert.Null(SnapPoints.Empty.Nearest(S(1), S(1)));
    }
}
