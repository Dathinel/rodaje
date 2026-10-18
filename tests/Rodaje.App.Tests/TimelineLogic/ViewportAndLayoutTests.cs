using Rodaje.App.Services;
using Rodaje.App.Views.Timeline;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Tests.TimelineLogic;

public class ViewportAndLayoutTests
{
    private static Time S(double seconds) => Time.FromSeconds(seconds);

    private static TimelineViewport Viewport(double pps = 100, double scroll = 0, double width = 1000) =>
        new() { PixelsPerSecond = pps, ScrollX = scroll, Width = width };

    [Fact]
    public void TiempoYPixelesSeConviertenEnLosDosSentidos()
    {
        TimelineViewport vp = Viewport(pps: 100, scroll: 250);

        Assert.Equal(-250, vp.TimeToX(Time.Zero));
        Assert.Equal(250, vp.TimeToX(S(5)));
        Assert.Equal(S(5), vp.XToTime(250));
        Assert.Equal(S(2.5), vp.VisibleStart);
        Assert.Equal(S(12.5), vp.VisibleEnd);
        Assert.Equal(Time.Zero, vp.XToTime(-1000));
        Assert.Equal(S(0.08), vp.PixelsToDuration(8));
    }

    [Fact]
    public void ElZoomSeLimitaYElDesplazamientoNoEsNegativo()
    {
        TimelineViewport vp = Viewport();
        vp.PixelsPerSecond = 1e9;
        Assert.Equal(TimelineState.MaxPixelsPerSecond, vp.PixelsPerSecond);
        vp.PixelsPerSecond = 0;
        Assert.Equal(TimelineState.MinPixelsPerSecond, vp.PixelsPerSecond);
        vp.PixelsPerSecond = double.NaN;
        Assert.Equal(TimelineState.DefaultPixelsPerSecond, vp.PixelsPerSecond);
        vp.ScrollX = -5;
        Assert.Equal(0, vp.ScrollX);
    }

    [Theory]
    [InlineData(100, 0, 300, 400)]
    [InlineData(60, 500, 10, 2000)]
    [InlineData(2000, 12345, 100, 50)]
    public void ZoomAlrededorDelCursorDejaQuietoElInstanteBajoEl(double pps, double scroll, double anchor, double newPps)
    {
        TimelineViewport vp = Viewport(pps, scroll);
        double before = (anchor + vp.ScrollX) / vp.PixelsPerSecond;

        vp.ZoomAround(anchor, newPps);

        Assert.Equal(newPps, vp.PixelsPerSecond, 6);
        Assert.Equal(before, (anchor + vp.ScrollX) / vp.PixelsPerSecond, 9);
    }

    [Fact]
    public void ZoomCercaDelInicioNoDejaDesplazamientoNegativo()
    {
        TimelineViewport vp = Viewport(pps: 100, scroll: 0);
        vp.ZoomAround(500, 10);
        Assert.Equal(0, vp.ScrollX);
    }

    [Fact]
    public void VerTodoYMantenerVisible()
    {
        TimelineViewport vp = Viewport(width: 1000);
        vp.FitTo(S(100));
        Assert.Equal(9.5, vp.PixelsPerSecond, 6);
        Assert.Equal(0, vp.ScrollX);
        Assert.True(vp.TimeToX(S(100)) <= 1000);

        vp.PixelsPerSecond = 100;
        Assert.True(vp.EnsureVisible(S(30), 40));
        Assert.Equal(960, vp.TimeToX(S(30)), 6);
        Assert.False(vp.EnsureVisible(S(25), 40));
    }

    [Fact]
    public void ElContenidoDejaEspacioDespuesDelFinal()
    {
        TimelineViewport vp = Viewport(pps: 100, width: 1000);
        Assert.Equal(1000, vp.ContentWidth(S(1)));
        Assert.Equal(6500, vp.ContentWidth(S(60)));
        vp.ScrollX = 99999;
        vp.ClampScroll(S(60));
        Assert.Equal(5500, vp.ScrollX);
    }

    [Theory]
    [InlineData(2400, true, 2)]
    [InlineData(600, true, 5)]
    [InlineData(100, false, 1)]
    [InlineData(60, false, 2)]
    [InlineData(10, false, 10)]
    [InlineData(2, false, 60)]
    public void LaReglaEligeUnPasoLegible(double pps, bool frames, double expectedMajorSeconds)
    {
        RulerStep step = RulerScale.ChooseStep(pps, Rational.Fps30);

        Assert.Equal(frames, step.ShowsFrames);
        double major = step.Major.ToSeconds();
        if (frames)
        {
            Assert.Equal(expectedMajorSeconds, major * 30, 6);
        }
        else
        {
            Assert.Equal(expectedMajorSeconds, major, 6);
        }

        Assert.True(major * pps >= RulerScale.MinLabelSpacing);
        Assert.True(step.Divisions == 1 || major / step.Divisions * pps >= RulerScale.MinTickSpacing);
    }

    [Fact]
    public void LasMarcasCubrenLoVisibleConMayoresCadaPaso()
    {
        TimelineViewport vp = Viewport(pps: 100, scroll: 150, width: 1000);
        RulerStep step = RulerScale.ChooseStep(vp.PixelsPerSecond, Rational.Fps30);

        List<RulerTick> ticks = RulerScale.Ticks(vp, step);

        Assert.True(ticks[0].X <= 0);
        Assert.True(ticks[^1].X >= 1000);
        Assert.All(ticks.Where(t => t.IsMajor), t => Assert.Equal(0, t.Time.Ticks % step.Major.Ticks));
        Assert.Equal(ticks.Count(t => t.IsMajor), ticks.Count(t => t.Time.Ticks % step.Major.Ticks == 0));
        for (int i = 1; i < ticks.Count; i++)
        {
            Assert.Equal(step.Minor.Ticks, ticks[i].Time.Ticks - ticks[i - 1].Time.Ticks);
        }
    }

    [Fact]
    public void EtiquetasDeLaRegla()
    {
        var seconds = new RulerStep(S(1), 2, false);
        var frames = new RulerStep(Time.FromFrames(5, Rational.Fps30), 5, true);

        Assert.Equal("0:00", RulerScale.FormatLabel(Time.Zero, seconds, Rational.Fps30));
        Assert.Equal("1:05", RulerScale.FormatLabel(S(65), seconds, Rational.Fps30));
        Assert.Equal("1:00:01", RulerScale.FormatLabel(S(3601), seconds, Rational.Fps30));
        Assert.Equal("0:02:15", RulerScale.FormatLabel(Time.FromFrames(75, Rational.Fps30), frames, Rational.Fps30));
        Assert.Equal("0:01:29", RulerScale.FormatLabel(Time.FromFrames(59, Rational.Ntsc30), frames, Rational.Ntsc30));
    }

    [Fact]
    public void LasFilasVanDeLaPistaDeVideoMasAltaAlAudio()
    {
        Sequence sequence = ProjectFactory.CreateDefault("p", new SequentialIdGenerator()).Sequences[0];
        Track main = sequence.VideoTracks[0];

        TrackLayout layout = TrackLayout.Build(sequence, main);

        Assert.Equal(["Video 2", "Video 1", "Audio 1", "Audio 2"], layout.Rows.Select(r => r.Track.Name));
        Assert.Equal([false, true, false, false], layout.Rows.Select(r => r.IsMain));
        Assert.Equal(TrackLayout.MainRowHeight, layout.Rows[1].Height);
        Assert.Equal(TrackLayout.VideoRowHeight, layout.Rows[0].Height);
        Assert.Equal(TrackLayout.AudioRowHeight, layout.Rows[2].Height);
        Assert.True(layout.Rows[2].Top - layout.Rows[1].Bottom >= TrackLayout.SectionGap);
        Assert.Equal(layout.Rows[^1].Bottom + TrackLayout.RowGap + TrackLayout.TopPadding, layout.TotalHeight, 6);

        Assert.Same(layout.Rows[1], layout.RowAt(layout.Rows[1].Top + 1));
        Assert.Same(layout.Rows[1], layout.RowAt(layout.Rows[1].Bottom + 1));
        Assert.Same(layout.Rows[0], layout.RowAt(-50));
        Assert.Same(layout.Rows[3], layout.RowAt(5000));
        Assert.Null(layout.RowStrictlyAt(layout.Rows[1].Bottom + 1));
        Assert.Same(layout.Rows[2], layout.Find(sequence.AudioTracks[0].Id));
    }

    [Fact]
    public void ArrastrarUnaCabeceraSoloLaLlevaEntreLasDeSuTipoYNuncaBajoLaPrincipal()
    {
        Sequence sequence = ProjectFactory.CreateDefault("p", new SequentialIdGenerator()).Sequences[0];
        sequence.VideoTracks.Add(new Track { Id = "v3", Kind = TrackKind.Video, Name = "Video 3" });
        TrackLayout layout = TrackLayout.Build(sequence, sequence.VideoTracks[0]);
        TrackRow top = layout.Rows[0];
        TrackRow main = layout.Find(sequence.VideoTracks[0].Id)!;
        string v2 = sequence.VideoTracks[1].Id;

        Assert.Equal(2, layout.IndexInKind("v3"));
        Assert.Equal(0, layout.IndexInKind(main.TrackId));
        Assert.Equal((2, top.Top - (TrackLayout.RowGap / 2)), layout.ReorderSlot(v2, top.Top + 1));
        Assert.Equal(1, layout.ReorderSlot("v3", main.Bottom + 200)!.Value.Index);

        string a1 = sequence.AudioTracks[0].Id;
        TrackRow lastAudio = layout.Rows[^1];
        Assert.Equal(1, layout.ReorderSlot(a1, lastAudio.Bottom)!.Value.Index);
        Assert.Equal(0, layout.ReorderSlot(a1, -100)!.Value.Index);
    }
}
