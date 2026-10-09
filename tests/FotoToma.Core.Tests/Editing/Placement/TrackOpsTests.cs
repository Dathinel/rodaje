using FotoToma.Core.Editing;
using FotoToma.Core.Model;
using FotoToma.Core.Timing;
using static FotoToma.Core.Tests.Editing.Placement.PlacementBuilder;

namespace FotoToma.Core.Tests.Editing.Placement;

public class TrackOpsTests
{
    [Fact]
    public void Frame_at_25_fps_is_an_exact_number_of_ticks()
    {
        Assert.Equal(28_224_000L, F(1).Ticks);
        Assert.Equal(282_240_000L, F(10).Ticks);
    }

    [Fact]
    public void ClearRange_removes_clip_fully_inside()
    {
        var b = new PlacementBuilder();
        b.Add("v1", "c1", 10, 0, 10);
        b.Add("v1", "c2", 30, 0, 10);

        TrackOps.ClearRange(b.Context(), b.Track("v1"), F(10), F(20));

        Assert.Equal("c2@30[0,10)", b.Layout("v1"));
        b.AssertValid();
    }

    [Fact]
    public void ClearRange_trims_out_of_clip_straddling_from()
    {
        var b = new PlacementBuilder();
        b.Add("v1", "c1", 0, 5, 25);

        TrackOps.ClearRange(b.Context(), b.Track("v1"), F(10), F(30));

        Clip c1 = b.Clip("c1");
        Assert.Equal(0L, c1.Start.Ticks);
        Assert.Equal(5 * FrameTicks, c1.SourceIn.Ticks);
        Assert.Equal(15 * FrameTicks, c1.SourceOut.Ticks);
        b.AssertValid();
    }

    [Fact]
    public void ClearRange_trims_in_of_clip_straddling_to()
    {
        var b = new PlacementBuilder();
        b.Add("v1", "c1", 10, 5, 25);

        TrackOps.ClearRange(b.Context(), b.Track("v1"), F(0), F(15));

        Clip c1 = b.Clip("c1");
        Assert.Equal(15 * FrameTicks, c1.Start.Ticks);
        Assert.Equal(10 * FrameTicks, c1.SourceIn.Ticks);
        Assert.Equal(25 * FrameTicks, c1.SourceOut.Ticks);
        b.AssertValid();
    }

    [Fact]
    public void ClearRange_splits_clip_covering_whole_range()
    {
        var b = new PlacementBuilder();
        Clip c1 = b.Add("v1", "c1", 10, 0, 40, link: "L");
        c1.Effects.Add(new EffectInstance
        {
            Id = "fx-orig",
            Type = "fototoma.transform",
            Parameters = { ["x"] = new Parameter { Value = 1, Keyframes = [new Keyframe(F(2), 1)] } },
        });
        b.Add("a1", "c1a", 10, 0, 40, link: "L");

        TrackOps.ClearRange(b.Context(), b.Track("v1"), F(20), F(30));

        // Ids: clip-1 para la mitad derecha, link-2 para su enlace derivado y fx-3 para la copia del efecto.
        Assert.Equal("c1@10[0,10) clip-1@30[20,40)", b.Layout("v1"));
        Clip right = b.Clip("clip-1");
        Assert.Equal("L", c1.LinkId);
        Assert.Equal("link-2", right.LinkId);
        Assert.Equal("fx-orig", c1.Effects[0].Id);
        Assert.Equal("fx-3", right.Effects[0].Id);
        Assert.NotSame(c1.Effects[0], right.Effects[0]);
        Assert.NotSame(c1.Effects[0].Parameters["x"], right.Effects[0].Parameters["x"]);
        b.AssertValid();
    }

    [Fact]
    public void ClearRange_split_of_unlinked_clip_keeps_no_link()
    {
        var b = new PlacementBuilder();
        b.Add("v1", "c1", 0, 0, 40);

        TrackOps.ClearRange(b.Context(), b.Track("v1"), F(10), F(20));

        Assert.Equal("c1@0[0,10) clip-1@20[20,40)", b.Layout("v1"));
        Assert.Null(b.Clip("clip-1").LinkId);
        b.AssertValid();
    }

    [Fact]
    public void ClearRange_leaves_touching_clips_alone()
    {
        var b = new PlacementBuilder();
        b.Add("v1", "c1", 0, 0, 10);
        b.Add("v1", "c2", 20, 0, 10);

        TrackOps.ClearRange(b.Context(), b.Track("v1"), F(10), F(20));

        Assert.Equal("c1@0[0,10) c2@20[0,10)", b.Layout("v1"));
    }

    [Fact]
    public void ClearRange_with_empty_range_does_nothing()
    {
        var b = new PlacementBuilder();
        b.Add("v1", "c1", 0, 0, 40);

        TrackOps.ClearRange(b.Context(), b.Track("v1"), F(10), F(10));

        Assert.Equal("c1@0[0,40)", b.Layout("v1"));
    }

    [Fact]
    public void ClearRange_handles_all_four_cases_at_once()
    {
        var b = new PlacementBuilder();
        b.Add("v1", "left", 0, 0, 15);
        b.Add("v1", "inside", 15, 0, 5);
        b.Add("v1", "right", 20, 0, 20);

        TrackOps.ClearRange(b.Context(), b.Track("v1"), F(10), F(30));

        Assert.Equal("left@0[0,10) right@30[10,20)", b.Layout("v1"));
        b.AssertValid();
    }

    [Fact]
    public void InsertSpace_splits_clip_containing_point_and_shifts_rest()
    {
        var b = new PlacementBuilder();
        b.Add("v1", "c1", 0, 10, 30, link: "L");
        b.Add("v1", "c2", 25, 0, 10);

        TrackOps.InsertSpace(b.Context(), b.Track("v1"), F(5), F(7));

        Assert.Equal("c1@0[10,15) clip-1@12[15,30) c2@32[0,10)", b.Layout("v1"));
        Assert.Equal("link-2", b.Clip("clip-1").LinkId);
        b.AssertValid();
    }

    [Fact]
    public void InsertSpace_at_clip_start_shifts_without_splitting()
    {
        var b = new PlacementBuilder();
        b.Add("v1", "c1", 0, 0, 10);
        b.Add("v1", "c2", 10, 0, 10);

        TrackOps.InsertSpace(b.Context(), b.Track("v1"), F(10), F(5));

        Assert.Equal("c1@0[0,10) c2@15[0,10)", b.Layout("v1"));
    }

    [Fact]
    public void InsertSpace_in_a_gap_only_shifts_later_clips()
    {
        var b = new PlacementBuilder();
        b.Add("v1", "c1", 0, 0, 10);
        b.Add("v1", "c2", 20, 0, 10);

        TrackOps.InsertSpace(b.Context(), b.Track("v1"), F(15), F(3));

        Assert.Equal("c1@0[0,10) c2@23[0,10)", b.Layout("v1"));
    }

    [Fact]
    public void InsertSpace_zero_duration_does_nothing()
    {
        var b = new PlacementBuilder();
        b.Add("v1", "c1", 0, 0, 10);

        TrackOps.InsertSpace(b.Context(), b.Track("v1"), F(5), Time.Zero);

        Assert.Equal("c1@0[0,10)", b.Layout("v1"));
    }

    [Fact]
    public void InsertSpace_negative_duration_throws()
    {
        var b = new PlacementBuilder();
        Assert.Throws<ArgumentOutOfRangeException>(() => TrackOps.InsertSpace(b.Context(), b.Track("v1"), F(5), -F(1)));
    }

    [Fact]
    public void AddClipSorted_places_clip_by_start()
    {
        var b = new PlacementBuilder();
        b.Add("v1", "c3", 40, 0, 5);
        b.Add("v1", "c1", 0, 0, 5);
        b.Add("v1", "c2", 20, 0, 5);
        b.Add("v1", "c4", 60, 0, 5);

        Assert.Equal("c1@0[0,5) c2@20[0,5) c3@40[0,5) c4@60[0,5)", b.Layout("v1"));
    }

    [Fact]
    public void MergeSpans_joins_touching_and_overlapping()
    {
        List<(Time Start, Time End)> merged = TrackOps.MergeSpans(
        [
            (F(30), F(40)),
            (F(0), F(10)),
            (F(10), F(15)),
            (F(12), F(14)),
            (F(50), F(60)),
        ]);

        Assert.Equal([(F(0), F(15)), (F(30), F(40)), (F(50), F(60))], merged);
    }

    [Fact]
    public void ResolveLinkGroup_unknown_clip_throws_edit_exception()
    {
        var b = new PlacementBuilder();
        EditException ex = Assert.Throws<EditException>(() => TrackOps.ResolveLinkGroup(b.Context(), "nada"));
        Assert.Equal("No existe el clip «nada» en la secuencia.", ex.Message);
    }

    [Fact]
    public void KindName_is_spanish()
    {
        Assert.Equal("video", TrackOps.KindName(TrackKind.Video));
        Assert.Equal("audio", TrackOps.KindName(TrackKind.Audio));
    }
}
