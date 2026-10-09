using Rodaje.Core.Editing;
using static Rodaje.Core.Tests.Editing.Placement.PlacementBuilder;

namespace Rodaje.Core.Tests.Editing.Placement;

public class RemovalTests
{
    private static void Fails(PlacementBuilder b, Action action, string expectedMessagePart)
    {
        string before = b.Snapshot();
        EditException ex = Assert.Throws<EditException>(action);
        Assert.Contains(expectedMessagePart, ex.Message, StringComparison.Ordinal);
        Assert.Equal(before, b.Snapshot());
    }

    [Fact]
    public void Lift_leaves_gap()
    {
        var b = new PlacementBuilder();
        b.Add("v1", "c1", 0, 0, 10);
        b.Add("v1", "c2", 10, 0, 10);
        b.Add("v1", "c3", 20, 0, 10);

        TimelineEdits.Lift(b.Context(), ["c2"]);

        Assert.Equal("c1@0[0,10) c3@20[0,10)", b.Layout("v1"));
        b.AssertValid();
    }

    [Fact]
    public void Lift_removes_link_group_and_deduplicates()
    {
        var b = new PlacementBuilder();
        b.Add("v1", "v", 0, 0, 10, link: "L");
        b.Add("a1", "a", 0, 0, 10, link: "L");
        b.Add("v1", "x", 10, 0, 10);

        TimelineEdits.Lift(b.Context(), ["v", "a", "v"]);

        Assert.Equal("x@10[0,10)", b.Layout("v1"));
        Assert.Equal("", b.Layout("a1"));
        b.AssertValid();
    }

    [Fact]
    public void Lift_unknown_clip_fails()
    {
        var b = new PlacementBuilder();
        b.Add("v1", "c1", 0, 0, 10);
        Fails(b, () => TimelineEdits.Lift(b.Context(), ["c1", "nada"]), "No existe el clip «nada» en la secuencia.");
    }

    [Fact]
    public void Lift_empty_selection_fails()
    {
        var b = new PlacementBuilder();
        Fails(b, () => TimelineEdits.Lift(b.Context(), []), "No hay ningún clip seleccionado.");
    }

    [Fact]
    public void Lift_on_locked_track_fails()
    {
        var b = new PlacementBuilder();
        b.Add("v1", "v", 0, 0, 10, link: "L");
        b.Add("a1", "a", 0, 0, 10, link: "L");
        b.Track("a1").Locked = true;
        Fails(b, () => TimelineEdits.Lift(b.Context(), ["v"]), "La pista «Audio 1» está bloqueada.");
    }

    [Fact]
    public void RippleDelete_track_scope_closes_gap_only_in_that_track()
    {
        var b = new PlacementBuilder();
        b.Add("v1", "c1", 0, 0, 10);
        b.Add("v1", "c2", 10, 0, 5);
        b.Add("v1", "c3", 20, 0, 10);
        b.Add("v2", "o", 20, 0, 10);

        TimelineEdits.RippleDelete(b.Context(), ["c2"], RippleScope.Track);

        Assert.Equal("c1@0[0,10) c3@15[0,10)", b.Layout("v1"));
        Assert.Equal("o@20[0,10)", b.Layout("v2"));
        Assert.Equal(15 * FrameTicks, b.Clip("c3").Start.Ticks);
        b.AssertValid();
    }

    [Fact]
    public void RippleDelete_track_scope_with_link_closes_both_tracks()
    {
        var b = new PlacementBuilder();
        b.Add("v1", "v", 0, 0, 10, link: "L");
        b.Add("a1", "a", 0, 0, 10, link: "L");
        b.Add("v1", "v2c", 10, 0, 10);
        b.Add("a1", "a2c", 12, 0, 10);
        b.Add("a2", "music", 10, 0, 10, media: "aud");

        TimelineEdits.RippleDelete(b.Context(), ["a"], RippleScope.Track);

        Assert.Equal("v2c@0[0,10)", b.Layout("v1"));
        Assert.Equal("a2c@2[0,10)", b.Layout("a1"));
        Assert.Equal("music@10[0,10)", b.Layout("a2"));
        b.AssertValid();
    }

    [Fact]
    public void RippleDelete_multi_select_same_track_processes_right_to_left()
    {
        var b = new PlacementBuilder();
        b.Add("v1", "c1", 0, 0, 10);
        b.Add("v1", "c2", 10, 0, 5);
        b.Add("v1", "c3", 15, 0, 10);
        b.Add("v1", "c4", 30, 0, 3);
        b.Add("v1", "c5", 40, 0, 10);

        TimelineEdits.RippleDelete(b.Context(), ["c2", "c4"], RippleScope.Track);

        Assert.Equal("c1@0[0,10) c3@10[0,10) c5@32[0,10)", b.Layout("v1"));
        b.AssertValid();
    }

    [Fact]
    public void RippleDelete_all_tracks_shifts_every_unlocked_track()
    {
        var b = new PlacementBuilder();
        b.Add("v1", "c1", 0, 0, 10);
        b.Add("v1", "c2", 10, 0, 10);
        b.Add("v2", "o", 25, 0, 10);
        b.Add("a2", "m", 20, 0, 10, media: "aud");

        TimelineEdits.RippleDelete(b.Context(), ["c1"], RippleScope.AllTracks);

        Assert.Equal("c2@0[0,10)", b.Layout("v1"));
        Assert.Equal("o@15[0,10)", b.Layout("v2"));
        Assert.Equal("m@10[0,10)", b.Layout("a2"));
        b.AssertValid();
    }

    [Fact]
    public void RippleDelete_all_tracks_linked_pair_shifts_only_once()
    {
        var b = new PlacementBuilder();
        b.Add("v1", "v", 0, 0, 10, link: "L");
        b.Add("a1", "a", 0, 0, 10, link: "L");
        b.Add("v1", "next", 10, 0, 10);
        b.Add("v2", "o", 30, 0, 10);

        TimelineEdits.RippleDelete(b.Context(), ["v"], RippleScope.AllTracks);

        Assert.Equal("next@0[0,10)", b.Layout("v1"));
        Assert.Equal("", b.Layout("a1"));
        Assert.Equal("o@20[0,10)", b.Layout("v2"));
        b.AssertValid();
    }

    [Fact]
    public void RippleDelete_all_tracks_multi_select_disjoint_spans()
    {
        var b = new PlacementBuilder();
        b.Add("v1", "c1", 0, 0, 10);
        b.Add("v1", "c2", 10, 0, 10);
        b.Add("v1", "c3", 20, 0, 10);
        b.Add("v1", "c4", 30, 0, 10);
        b.Add("a1", "x", 40, 0, 5);

        TimelineEdits.RippleDelete(b.Context(), ["c3", "c1"], RippleScope.AllTracks);

        Assert.Equal("c2@0[0,10) c4@10[0,10)", b.Layout("v1"));
        Assert.Equal("x@20[0,5)", b.Layout("a1"));
        b.AssertValid();
    }

    [Fact]
    public void RippleDelete_all_tracks_straddling_clip_fails()
    {
        var b = new PlacementBuilder();
        b.Add("v1", "c1", 10, 0, 10);
        b.Add("v1", "c2", 20, 0, 10);
        b.Add("a1", "long", 5, 0, 30, media: "aud");

        Fails(b, () => TimelineEdits.RippleDelete(b.Context(), ["c1"], RippleScope.AllTracks),
            "No se puede cerrar el hueco porque en la pista «Audio 1» hay un clip que atraviesa ese tramo.");
    }

    [Fact]
    public void RippleDelete_track_scope_ignores_other_tracks_straddling()
    {
        var b = new PlacementBuilder();
        b.Add("v1", "c1", 10, 0, 10);
        b.Add("v1", "c2", 20, 0, 10);
        b.Add("a1", "long", 5, 0, 30, media: "aud");

        TimelineEdits.RippleDelete(b.Context(), ["c1"], RippleScope.Track);

        Assert.Equal("c2@10[0,10)", b.Layout("v1"));
        Assert.Equal("long@5[0,30)", b.Layout("a1"));
        b.AssertValid();
    }

    [Fact]
    public void RippleDelete_all_tracks_skips_locked_tracks()
    {
        var b = new PlacementBuilder();
        b.Add("v1", "c1", 10, 0, 10);
        b.Add("v1", "c2", 20, 0, 10);
        b.Add("a1", "long", 5, 0, 30, media: "aud");
        b.Add("a1", "late", 50, 0, 10, media: "aud");
        b.Add("a2", "free", 40, 0, 10, media: "aud");
        b.Track("a1").Locked = true;

        TimelineEdits.RippleDelete(b.Context(), ["c1"], RippleScope.AllTracks);

        Assert.Equal("c2@10[0,10)", b.Layout("v1"));
        Assert.Equal("long@5[0,30) late@50[0,10)", b.Layout("a1"));
        Assert.Equal("free@30[0,10)", b.Layout("a2"));
        b.AssertValid();
    }

    [Fact]
    public void RippleDelete_on_locked_track_fails()
    {
        var b = new PlacementBuilder();
        b.Add("v1", "c1", 0, 0, 10);
        b.Track("v1").Locked = true;
        Fails(b, () => TimelineEdits.RippleDelete(b.Context(), ["c1"], RippleScope.Track), "La pista «Video 1» está bloqueada.");
    }

    [Fact]
    public void RippleDelete_unknown_or_empty_fails()
    {
        var b = new PlacementBuilder();
        Fails(b, () => TimelineEdits.RippleDelete(b.Context(), ["x"], RippleScope.Track), "No existe el clip «x»");
        Fails(b, () => TimelineEdits.RippleDelete(b.Context(), [], RippleScope.AllTracks), "No hay ningún clip seleccionado.");
    }

    [Fact]
    public void CloseGap_middle_gap_track_scope()
    {
        var b = new PlacementBuilder();
        b.Add("v1", "c1", 0, 0, 10);
        b.Add("v1", "c2", 18, 0, 10);
        b.Add("v1", "c3", 30, 0, 10);
        b.Add("v2", "o", 40, 0, 10);

        TimelineEdits.CloseGap(b.Context(), "v1", F(12), RippleScope.Track);

        Assert.Equal("c1@0[0,10) c2@10[0,10) c3@22[0,10)", b.Layout("v1"));
        Assert.Equal("o@40[0,10)", b.Layout("v2"));
        b.AssertValid();
    }

    [Fact]
    public void CloseGap_leading_gap()
    {
        var b = new PlacementBuilder();
        b.Add("v1", "c1", 7, 0, 10);

        TimelineEdits.CloseGap(b.Context(), "v1", F(0), RippleScope.Track);

        Assert.Equal("c1@0[0,10)", b.Layout("v1"));
        Assert.Equal(0L, b.Clip("c1").Start.Ticks);
        b.AssertValid();
    }

    [Fact]
    public void CloseGap_at_exact_gap_start_counts()
    {
        var b = new PlacementBuilder();
        b.Add("v1", "c1", 0, 0, 10);
        b.Add("v1", "c2", 15, 0, 10);

        TimelineEdits.CloseGap(b.Context(), "v1", F(10), RippleScope.Track);

        Assert.Equal("c1@0[0,10) c2@10[0,10)", b.Layout("v1"));
        b.AssertValid();
    }

    [Theory]
    [InlineData(5)]   // dentro de un clip
    [InlineData(15)]  // al inicio de un clip (no es hueco)
    [InlineData(30)]  // después del último clip
    [InlineData(-1)]  // antes del inicio de la línea de tiempo
    public void CloseGap_without_gap_fails(long frame)
    {
        var b = new PlacementBuilder();
        b.Add("v1", "c1", 0, 0, 10);
        b.Add("v1", "c2", 15, 0, 10);
        Fails(b, () => TimelineEdits.CloseGap(b.Context(), "v1", F(frame), RippleScope.Track), "No hay un hueco en la pista «Video 1»");
    }

    [Fact]
    public void CloseGap_on_empty_track_fails()
    {
        var b = new PlacementBuilder();
        Fails(b, () => TimelineEdits.CloseGap(b.Context(), "v1", F(0), RippleScope.Track), "No hay un hueco");
    }

    [Fact]
    public void CloseGap_all_tracks_shifts_others()
    {
        var b = new PlacementBuilder();
        b.Add("v1", "c1", 0, 0, 10);
        b.Add("v1", "c2", 15, 0, 10);
        b.Add("a1", "a", 15, 0, 10);
        b.Add("v2", "early", 0, 0, 10);
        b.Track("a2").Locked = true;
        b.Add("a2", "locked", 20, 0, 10);

        TimelineEdits.CloseGap(b.Context(), "v1", F(12), RippleScope.AllTracks);

        Assert.Equal("c1@0[0,10) c2@10[0,10)", b.Layout("v1"));
        Assert.Equal("a@10[0,10)", b.Layout("a1"));
        Assert.Equal("early@0[0,10)", b.Layout("v2"));
        Assert.Equal("locked@20[0,10)", b.Layout("a2"));
        b.AssertValid();
    }

    [Fact]
    public void CloseGap_all_tracks_straddling_fails()
    {
        var b = new PlacementBuilder();
        b.Add("v1", "c1", 0, 0, 10);
        b.Add("v1", "c2", 15, 0, 10);
        b.Add("v2", "cross", 8, 0, 10);

        Fails(b, () => TimelineEdits.CloseGap(b.Context(), "v1", F(12), RippleScope.AllTracks),
            "No se puede cerrar el hueco porque en la pista «Video 2» hay un clip que atraviesa ese tramo.");
    }

    [Fact]
    public void CloseGap_locked_or_unknown_track_fails()
    {
        var b = new PlacementBuilder();
        b.Add("v1", "c1", 5, 0, 10);
        Fails(b, () => TimelineEdits.CloseGap(b.Context(), "v8", F(0), RippleScope.Track), "No existe la pista «v8» en la secuencia.");
        b.Track("v1").Locked = true;
        Fails(b, () => TimelineEdits.CloseGap(b.Context(), "v1", F(0), RippleScope.Track), "La pista «Video 1» está bloqueada.");
    }

    [Fact]
    public void Null_arguments_throw()
    {
        var b = new PlacementBuilder();
        Assert.Throws<ArgumentNullException>(() => TimelineEdits.Lift(null!, ["c"]));
        Assert.Throws<ArgumentNullException>(() => TimelineEdits.RippleDelete(null!, ["c"], RippleScope.Track));
        Assert.Throws<ArgumentNullException>(() => TimelineEdits.CloseGap(null!, "v1", F(0), RippleScope.Track));
        Assert.Throws<ArgumentNullException>(() => TimelineEdits.Lift(b.Context(), null!));
    }
}
