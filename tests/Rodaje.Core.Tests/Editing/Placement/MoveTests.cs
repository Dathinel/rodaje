using Rodaje.Core.Editing;
using static Rodaje.Core.Tests.Editing.Placement.PlacementBuilder;

namespace Rodaje.Core.Tests.Editing.Placement;

public class MoveTests
{
    private static void Fails(PlacementBuilder b, Action action, string expectedMessagePart)
    {
        string before = b.Snapshot();
        EditException ex = Assert.Throws<EditException>(action);
        Assert.Contains(expectedMessagePart, ex.Message, StringComparison.Ordinal);
        Assert.Equal(before, b.Snapshot());
    }

    [Fact]
    public void Strict_move_to_free_spot()
    {
        var b = new PlacementBuilder();
        b.Add("v1", "c1", 0, 0, 10);
        b.Add("v1", "c2", 20, 0, 10);

        TimelineEdits.Move(b.Context(), "c1", F(40), null, PlacementMode.Strict);

        Assert.Equal("c2@20[0,10) c1@40[0,10)", b.Layout("v1"));
        Assert.Equal(40 * FrameTicks, b.Clip("c1").Start.Ticks);
        b.AssertValid();
    }

    [Fact]
    public void Strict_move_over_own_position_is_allowed()
    {
        var b = new PlacementBuilder();
        b.Add("v1", "c1", 0, 0, 10);

        TimelineEdits.Move(b.Context(), "c1", F(5), null, PlacementMode.Strict);

        Assert.Equal("c1@5[0,10)", b.Layout("v1"));
        b.AssertValid();
    }

    [Fact]
    public void Strict_move_onto_occupied_spot_names_blocking_clip()
    {
        var b = new PlacementBuilder();
        b.Add("v1", "c1", 0, 0, 10);
        b.Add("v1", "c2", 20, 0, 10);

        Fails(b, () => TimelineEdits.Move(b.Context(), "c1", F(15), null, PlacementMode.Strict),
            "El lugar está ocupado por el clip «c2» en la pista «Video 1».");
    }

    [Fact]
    public void Strict_move_to_other_track_blocked_by_clip_there()
    {
        var b = new PlacementBuilder();
        b.Add("v1", "c1", 0, 0, 10);
        b.Add("v2", "c2", 5, 0, 10);

        Fails(b, () => TimelineEdits.Move(b.Context(), "c1", F(0), "v2", PlacementMode.Strict),
            "El lugar está ocupado por el clip «c2» en la pista «Video 2».");
    }

    [Fact]
    public void Overwrite_move_trims_and_splits_what_is_below()
    {
        var b = new PlacementBuilder();
        b.Add("v1", "mover", 0, 0, 10);
        b.Add("v2", "big", 0, 0, 60);

        TimelineEdits.Move(b.Context(), "mover", F(20), "v2", PlacementMode.Overwrite);

        Assert.Equal("", b.Layout("v1"));
        Assert.Equal("big@0[0,20) mover@20[0,10) clip-1@30[30,60)", b.Layout("v2"));
        b.AssertValid();
    }

    [Fact]
    public void Overwrite_move_removes_covered_and_trims_neighbours()
    {
        var b = new PlacementBuilder();
        b.Add("v1", "a", 0, 0, 12);
        b.Add("v1", "b", 12, 0, 4);
        b.Add("v1", "c", 16, 0, 10);
        b.Add("v1", "m", 50, 0, 10);

        TimelineEdits.Move(b.Context(), "m", F(10), null, PlacementMode.Overwrite);

        Assert.Equal("a@0[0,10) m@10[0,10) c@20[4,10)", b.Layout("v1"));
        b.AssertValid();
    }

    [Fact]
    public void Insert_move_splits_and_pushes_right()
    {
        var b = new PlacementBuilder();
        b.Add("v1", "base", 0, 0, 30);
        b.Add("v1", "after", 40, 0, 5);
        b.Add("v2", "m", 0, 0, 10);

        TimelineEdits.Move(b.Context(), "m", F(10), "v1", PlacementMode.Insert);

        Assert.Equal("base@0[0,10) m@10[0,10) clip-1@20[10,30) after@50[0,5)", b.Layout("v1"));
        Assert.Equal("", b.Layout("v2"));
        b.AssertValid();
    }

    [Fact]
    public void Insert_move_within_same_track_leaves_gap_behind()
    {
        var b = new PlacementBuilder();
        b.Add("v1", "m", 0, 0, 10);
        b.Add("v1", "x", 20, 0, 10);

        TimelineEdits.Move(b.Context(), "m", F(20), null, PlacementMode.Insert);

        Assert.Equal("m@20[0,10) x@30[0,10)", b.Layout("v1"));
        b.AssertValid();
    }

    [Fact]
    public void Move_to_other_track_of_same_kind()
    {
        var b = new PlacementBuilder();
        b.Add("v1", "c1", 0, 0, 10);

        TimelineEdits.Move(b.Context(), "c1", F(3), "v2", PlacementMode.Strict);

        Assert.Equal("", b.Layout("v1"));
        Assert.Equal("c1@3[0,10)", b.Layout("v2"));
        b.AssertValid();
    }

    [Fact]
    public void Move_to_own_track_by_id_works()
    {
        var b = new PlacementBuilder();
        b.Add("a1", "c1", 0, 0, 10);

        TimelineEdits.Move(b.Context(), "c1", F(3), "a1", PlacementMode.Strict);

        Assert.Equal("c1@3[0,10)", b.Layout("a1"));
        b.AssertValid();
    }

    [Fact]
    public void Move_to_track_of_other_kind_fails()
    {
        var b = new PlacementBuilder();
        b.Add("v1", "c1", 0, 0, 10);

        Fails(b, () => TimelineEdits.Move(b.Context(), "c1", F(0), "a1", PlacementMode.Strict),
            "El clip «c1» es de video y no puede ir a la pista de audio «Audio 1».");
    }

    [Fact]
    public void Move_to_unknown_track_fails()
    {
        var b = new PlacementBuilder();
        b.Add("v1", "c1", 0, 0, 10);

        Fails(b, () => TimelineEdits.Move(b.Context(), "c1", F(0), "v9", PlacementMode.Strict),
            "No existe la pista «v9» en la secuencia.");
    }

    [Fact]
    public void Move_unknown_clip_fails()
    {
        var b = new PlacementBuilder();
        Fails(b, () => TimelineEdits.Move(b.Context(), "nada", F(0), null, PlacementMode.Strict),
            "No existe el clip «nada»");
    }

    [Fact]
    public void Move_to_negative_start_fails()
    {
        var b = new PlacementBuilder();
        b.Add("v1", "c1", 5, 0, 10);

        Fails(b, () => TimelineEdits.Move(b.Context(), "c1", -F(1), null, PlacementMode.Overwrite),
            "antes del inicio de la línea de tiempo");
    }

    [Fact]
    public void Linked_member_pushed_before_zero_fails()
    {
        var b = new PlacementBuilder();
        b.Add("v1", "v", 10, 0, 10, link: "L");
        b.Add("a1", "a", 4, 0, 10, link: "L");

        Fails(b, () => TimelineEdits.Move(b.Context(), "v", F(5), null, PlacementMode.Strict),
            "El clip enlazado «a» quedaría antes del inicio de la línea de tiempo.");
    }

    [Fact]
    public void Linked_group_moves_together_and_members_keep_their_tracks()
    {
        var b = new PlacementBuilder();
        b.Add("v1", "v", 10, 0, 20, link: "L");
        b.Add("a1", "a", 10, 0, 20, link: "L");

        TimelineEdits.Move(b.Context(), "v", F(30), "v2", PlacementMode.Strict);

        Assert.Equal("", b.Layout("v1"));
        Assert.Equal("v@30[0,20)", b.Layout("v2"));
        Assert.Equal("a@30[0,20)", b.Layout("a1"));
        b.AssertValid();
    }

    [Fact]
    public void Moving_audio_member_drags_video_member()
    {
        var b = new PlacementBuilder();
        b.Add("v1", "v", 10, 0, 20, link: "L");
        b.Add("a1", "a", 10, 0, 20, link: "L");

        TimelineEdits.Move(b.Context(), "a", F(2), "a2", PlacementMode.Strict);

        Assert.Equal("v@2[0,20)", b.Layout("v1"));
        Assert.Equal("", b.Layout("a1"));
        Assert.Equal("a@2[0,20)", b.Layout("a2"));
        b.AssertValid();
    }

    [Fact]
    public void Strict_linked_move_blocked_on_member_track()
    {
        var b = new PlacementBuilder();
        b.Add("v1", "v", 0, 0, 10, link: "L");
        b.Add("a1", "a", 0, 0, 10, link: "L");
        b.Add("a1", "music", 25, 0, 10, media: "aud");

        Fails(b, () => TimelineEdits.Move(b.Context(), "v", F(20), null, PlacementMode.Strict),
            "El lugar está ocupado por el clip «music» en la pista «Audio 1».");
    }

    [Fact]
    public void Overwrite_linked_move_clears_on_every_member_track()
    {
        var b = new PlacementBuilder();
        b.Add("v1", "v", 0, 0, 10, link: "L");
        b.Add("a1", "a", 0, 0, 10, link: "L");
        b.Add("v1", "vbig", 20, 0, 40, media: "vid");
        b.Add("a1", "abig", 20, 0, 40, media: "aud");

        TimelineEdits.Move(b.Context(), "v", F(30), null, PlacementMode.Overwrite);

        Assert.Equal("vbig@20[0,10) v@30[0,10) clip-1@40[20,40)", b.Layout("v1"));
        Assert.Equal("abig@20[0,10) a@30[0,10) clip-2@40[20,40)", b.Layout("a1"));
        b.AssertValid();
    }

    [Fact]
    public void Insert_linked_move_pushes_every_member_track()
    {
        var b = new PlacementBuilder();
        b.Add("v1", "v", 0, 0, 10, link: "L");
        b.Add("a1", "a", 0, 0, 10, link: "L");
        b.Add("v1", "v2c", 20, 0, 10);
        b.Add("a1", "a2c", 20, 0, 10);
        b.Add("v2", "untouched", 20, 0, 10);

        TimelineEdits.Move(b.Context(), "v", F(20), null, PlacementMode.Insert);

        Assert.Equal("v@20[0,10) v2c@30[0,10)", b.Layout("v1"));
        Assert.Equal("a@20[0,10) a2c@30[0,10)", b.Layout("a1"));
        Assert.Equal("untouched@20[0,10)", b.Layout("v2"));
        b.AssertValid();
    }

    [Fact]
    public void Overwrite_splitting_linked_pair_keeps_right_halves_linked()
    {
        var b = new PlacementBuilder();
        b.Add("v1", "v", 0, 0, 10, link: "L");
        b.Add("a1", "a", 0, 0, 10, link: "L");
        b.Add("v1", "bv", 20, 0, 40, link: "B");
        b.Add("a1", "ba", 20, 0, 40, link: "B");

        TimelineEdits.Move(b.Context(), "v", F(30), null, PlacementMode.Overwrite);

        Assert.Equal("bv@20[0,10) v@30[0,10) clip-1@40[20,40)", b.Layout("v1"));
        Assert.Equal("ba@20[0,10) a@30[0,10) clip-3@40[20,40)", b.Layout("a1"));
        Assert.Equal("link-2", b.Clip("clip-1").LinkId);
        Assert.Equal("link-2", b.Clip("clip-3").LinkId);
        b.AssertValid();
    }

    [Fact]
    public void Moving_primary_onto_track_of_linked_member_fails()
    {
        var b = new PlacementBuilder();
        b.Add("v1", "x", 0, 0, 10, link: "L");
        b.Add("v2", "y", 0, 0, 10, link: "L");

        Fails(b, () => TimelineEdits.Move(b.Context(), "x", F(30), "v2", PlacementMode.Overwrite),
            "La pista «Video 2» ya tiene un clip enlazado con «x»");
    }

    [Fact]
    public void Move_from_locked_track_fails()
    {
        var b = new PlacementBuilder();
        b.Add("v1", "c1", 0, 0, 10);
        b.Track("v1").Locked = true;

        Fails(b, () => TimelineEdits.Move(b.Context(), "c1", F(20), "v2", PlacementMode.Strict),
            "La pista «Video 1» está bloqueada.");
    }

    [Fact]
    public void Move_to_locked_track_fails()
    {
        var b = new PlacementBuilder();
        b.Add("v1", "c1", 0, 0, 10);
        b.Track("v2").Locked = true;

        Fails(b, () => TimelineEdits.Move(b.Context(), "c1", F(20), "v2", PlacementMode.Strict),
            "La pista «Video 2» está bloqueada.");
    }

    [Fact]
    public void Move_with_linked_member_on_locked_track_fails()
    {
        var b = new PlacementBuilder();
        b.Add("v1", "v", 0, 0, 10, link: "L");
        b.Add("a1", "a", 0, 0, 10, link: "L");
        b.Track("a1").Locked = true;

        Fails(b, () => TimelineEdits.Move(b.Context(), "v", F(20), null, PlacementMode.Strict),
            "La pista «Audio 1» está bloqueada.");
    }

    [Fact]
    public void Null_context_throws()
    {
        Assert.Throws<ArgumentNullException>(() => TimelineEdits.Move(null!, "c", F(0), null, PlacementMode.Strict));
    }
}
