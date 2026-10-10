using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using static Rodaje.Core.Tests.Editing.Placement.PlacementBuilder;

namespace Rodaje.Core.Tests.Editing.Placement;

/// <summary>El ripple de una pista corre también las pistas de los enlazados de lo que se corre, sin desincronizarlos.</summary>
public class LinkedRippleTests
{
    // V1 como pista principal: una imagen sin sonido seguida de una toma con su audio en A1, y música suelta en A2.
    private static PlacementBuilder MainTrackWithImage()
    {
        var b = new PlacementBuilder();
        b.Add("v1", "img", 0, 0, 10, media: "vid");
        b.Add("v1", "v", 10, 0, 20, link: "L");
        b.Add("a1", "a", 10, 0, 20, link: "L");
        b.Add("a2", "music", 40, 0, 10, media: "aud");
        return b;
    }

    [Fact]
    public void RippleDelete_track_scope_keeps_later_linked_audio_in_sync()
    {
        PlacementBuilder b = MainTrackWithImage();

        TimelineEdits.RippleDelete(b.Context(), ["img"], RippleScope.Track);

        Assert.Equal("v@0[0,20)", b.Layout("v1"));
        Assert.Equal("a@0[0,20)", b.Layout("a1"));
        Assert.Equal("music@40[0,10)", b.Layout("a2"));
        b.AssertValid();
    }

    [Fact]
    public void RippleDelete_track_scope_closes_each_track_once()
    {
        var b = new PlacementBuilder();
        b.Add("v1", "v0", 0, 0, 10, link: "L0");
        b.Add("a1", "a0", 0, 0, 10, link: "L0");
        b.Add("v1", "v1c", 10, 0, 10, link: "L1");
        b.Add("a2", "a1c", 10, 0, 10, link: "L1");
        b.Add("a1", "late", 30, 0, 10, media: "aud");

        TimelineEdits.RippleDelete(b.Context(), ["v0"], RippleScope.Track);

        Assert.Equal("v1c@0[0,10)", b.Layout("v1"));
        Assert.Equal("a1c@0[0,10)", b.Layout("a2"));
        Assert.Equal("late@20[0,10)", b.Layout("a1"));
        b.AssertValid();
    }

    [Fact]
    public void RippleDelete_track_scope_refuses_when_linked_track_cannot_close()
    {
        PlacementBuilder b = MainTrackWithImage();
        b.Add("a1", "jcut", 2, 0, 8, media: "aud");
        string before = b.Snapshot();

        EditException ex = Assert.Throws<EditException>(() => TimelineEdits.RippleDelete(b.Context(), ["img"], RippleScope.Track));

        Assert.Contains("«jcut» atraviesa", ex.Message, StringComparison.Ordinal);
        Assert.Equal(before, b.Snapshot());
    }

    [Fact]
    public void RippleDelete_track_scope_refuses_locked_linked_track()
    {
        PlacementBuilder b = MainTrackWithImage();
        b.Track("a1").Locked = true;
        string before = b.Snapshot();

        EditException ex = Assert.Throws<EditException>(() => TimelineEdits.RippleDelete(b.Context(), ["img"], RippleScope.Track));

        Assert.Contains("«Audio 1» está bloqueada", ex.Message, StringComparison.Ordinal);
        Assert.Equal(before, b.Snapshot());
    }

    [Fact]
    public void RippleTrim_track_scope_keeps_later_linked_audio_in_sync()
    {
        PlacementBuilder b = MainTrackWithImage();

        TimelineEdits.RippleTrim(b.Context(), "img", TrimEdge.Out, F(-4), RippleScope.Track);

        Assert.Equal("img@0[0,6) v@6[0,20)", b.Layout("v1"));
        Assert.Equal("a@6[0,20)", b.Layout("a1"));
        Assert.Equal("music@40[0,10)", b.Layout("a2"));
        b.AssertValid();
    }

    [Fact]
    public void RippleTrim_track_scope_lengthening_pushes_linked_audio()
    {
        PlacementBuilder b = MainTrackWithImage();

        TimelineEdits.RippleTrim(b.Context(), "img", TrimEdge.Out, F(5), RippleScope.Track);

        Assert.Equal("img@0[0,15) v@15[0,20)", b.Layout("v1"));
        Assert.Equal("a@15[0,20)", b.Layout("a1"));
        b.AssertValid();
    }

    [Fact]
    public void CloseGap_track_scope_keeps_later_linked_audio_in_sync()
    {
        var b = new PlacementBuilder();
        b.Add("v1", "c1", 0, 0, 10, media: "vid");
        b.Add("v1", "v", 15, 0, 10, link: "L");
        b.Add("a1", "a", 15, 0, 10, link: "L");

        TimelineEdits.CloseGap(b.Context(), "v1", F(12), RippleScope.Track);

        Assert.Equal("c1@0[0,10) v@10[0,10)", b.Layout("v1"));
        Assert.Equal("a@10[0,10)", b.Layout("a1"));
        b.AssertValid();
    }

    [Fact]
    public void Ripple_command_restores_everything_when_linked_track_cannot_close()
    {
        PlacementBuilder b = MainTrackWithImage();
        b.Add("a1", "jcut", 2, 0, 8, media: "aud");
        string before = b.Snapshot();
        var stack = new UndoStack(b.Project);

        Assert.Throws<EditException>(() => stack.Execute(Edits.RippleTrim(b.Sequence.Id, "img", TrimEdge.Out, F(-4), RippleScope.Track, b.Ids)));

        Assert.Equal(before, b.Snapshot());
    }

    [Fact]
    public void ReorderOnMainTrack_moves_linked_audio_of_every_shifted_clip()
    {
        PlacementBuilder b = MainTrackWithImage();

        TimelineEdits.ReorderOnMainTrack(b.Context(), "img", 1);

        Assert.Equal("v@0[0,20) img@20[0,10)", b.Layout("v1"));
        Assert.Equal("a@0[0,20)", b.Layout("a1"));
        b.AssertValid();
    }
}
