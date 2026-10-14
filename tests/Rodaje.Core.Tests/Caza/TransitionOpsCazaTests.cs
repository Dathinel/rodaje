using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Tests.Caza;

public class TransitionOpsCazaTests
{
    [Fact]
    public void MaxDuration_ReturnsClipsMinDuration_WhenNoNeighborTransitionsExist()
    {
        var track = new Track();
        var c1 = new Clip
        {
            Id = "c1",
            Start = Time.Zero,
            SourceIn = Time.Zero,
            SourceOut = Time.FromSeconds(6)
        };
        var c2 = new Clip
        {
            Id = "c2",
            Start = Time.FromSeconds(6),
            SourceIn = Time.Zero,
            SourceOut = Time.FromSeconds(10)
        };
        track.Clips.Add(c1);
        track.Clips.Add(c2);

        var transition = new TransitionInstance
        {
            From = "c1",
            To = "c2",
            Duration = Time.FromSeconds(2),
            Alignment = default
        };
        track.Transitions.Add(transition);

        Time max = TransitionOps.MaxDuration(track, transition);

        Assert.Equal(Time.FromSeconds(6), max);
    }

    [Fact]
    public void MaxDuration_LimitsDuration_WhenEnteringNeighborTransitionPrecedesCut()
    {
        var track = new Track();
        var c0 = new Clip
        {
            Id = "c0",
            Start = Time.Zero,
            SourceIn = Time.Zero,
            SourceOut = Time.FromSeconds(10)
        };
        var c1 = new Clip
        {
            Id = "c1",
            Start = Time.FromSeconds(10),
            SourceIn = Time.Zero,
            SourceOut = Time.FromSeconds(10)
        };
        var c2 = new Clip
        {
            Id = "c2",
            Start = Time.FromSeconds(20),
            SourceIn = Time.Zero,
            SourceOut = Time.FromSeconds(10)
        };
        track.Clips.Add(c0);
        track.Clips.Add(c1);
        track.Clips.Add(c2);

        var t01 = new TransitionInstance
        {
            From = "c0",
            To = "c1",
            Duration = Time.FromSeconds(2),
            Alignment = TransitionAlignment.After
        };
        var t12 = new TransitionInstance
        {
            From = "c1",
            To = "c2",
            Duration = Time.FromSeconds(1),
            Alignment = TransitionAlignment.Before
        };
        track.Transitions.Add(t01);
        track.Transitions.Add(t12);

        Time max = TransitionOps.MaxDuration(track, t12, TransitionAlignment.Before);

        Assert.Equal(Time.FromSeconds(8), max);
    }

    [Fact]
    public void MaxDuration_LimitsDuration_WhenLeavingNeighborTransitionFollowsCut()
    {
        var track = new Track();
        var c1 = new Clip
        {
            Id = "c1",
            Start = Time.Zero,
            SourceIn = Time.Zero,
            SourceOut = Time.FromSeconds(10)
        };
        var c2 = new Clip
        {
            Id = "c2",
            Start = Time.FromSeconds(10),
            SourceIn = Time.Zero,
            SourceOut = Time.FromSeconds(10)
        };
        var c3 = new Clip
        {
            Id = "c3",
            Start = Time.FromSeconds(20),
            SourceIn = Time.Zero,
            SourceOut = Time.FromSeconds(10)
        };
        track.Clips.Add(c1);
        track.Clips.Add(c2);
        track.Clips.Add(c3);

        var t12 = new TransitionInstance
        {
            From = "c1",
            To = "c2",
            Duration = Time.FromSeconds(1),
            Alignment = TransitionAlignment.After
        };
        var t23 = new TransitionInstance
        {
            From = "c2",
            To = "c3",
            Duration = Time.FromSeconds(2),
            Alignment = TransitionAlignment.Before
        };
        track.Transitions.Add(t12);
        track.Transitions.Add(t23);

        Time max = TransitionOps.MaxDuration(track, t12, TransitionAlignment.After);

        Assert.Equal(Time.FromSeconds(8), max);
    }

    [Fact]
    public void MaxDuration_LimitsCenteredDuration_WhenNeighborsReduceAvailableRoom()
    {
        var track = new Track();
        var c0 = new Clip
        {
            Id = "c0",
            Start = Time.Zero,
            SourceIn = Time.Zero,
            SourceOut = Time.FromSeconds(10)
        };
        var c1 = new Clip
        {
            Id = "c1",
            Start = Time.FromSeconds(10),
            SourceIn = Time.Zero,
            SourceOut = Time.FromSeconds(10)
        };
        var c2 = new Clip
        {
            Id = "c2",
            Start = Time.FromSeconds(20),
            SourceIn = Time.Zero,
            SourceOut = Time.FromSeconds(10)
        };
        var c3 = new Clip
        {
            Id = "c3",
            Start = Time.FromSeconds(30),
            SourceIn = Time.Zero,
            SourceOut = Time.FromSeconds(10)
        };
        track.Clips.Add(c0);
        track.Clips.Add(c1);
        track.Clips.Add(c2);
        track.Clips.Add(c3);

        var t01 = new TransitionInstance
        {
            From = "c0",
            To = "c1",
            Duration = Time.FromSeconds(8),
            Alignment = TransitionAlignment.After
        };
        var t12 = new TransitionInstance
        {
            From = "c1",
            To = "c2",
            Duration = Time.FromSeconds(1),
            Alignment = default
        };
        var t23 = new TransitionInstance
        {
            From = "c2",
            To = "c3",
            Duration = Time.FromSeconds(8),
            Alignment = TransitionAlignment.Before
        };
        track.Transitions.Add(t01);
        track.Transitions.Add(t12);
        track.Transitions.Add(t23);

        Time max = TransitionOps.MaxDuration(track, t12);

        Assert.Equal(Time.FromSeconds(4), max);
    }

    [Fact]
    public void Reconcile_MaintainsTransitionAtCut_WhenClipsRemainContiguous()
    {
        var sequence = new Sequence();
        var track = new Track();
        var c1 = new Clip
        {
            Id = "c1",
            Start = Time.Zero,
            SourceIn = Time.Zero,
            SourceOut = Time.FromSeconds(10)
        };
        var c2 = new Clip
        {
            Id = "c2",
            Start = Time.FromSeconds(10),
            SourceIn = Time.Zero,
            SourceOut = Time.FromSeconds(10)
        };
        track.Clips.Add(c1);
        track.Clips.Add(c2);

        var transition = new TransitionInstance
        {
            From = "c1",
            To = "c2",
            Duration = Time.FromSeconds(2),
            Alignment = default
        };
        track.Transitions.Add(transition);
        sequence.VideoTracks.Add(track);

        TransitionOps.Reconcile(sequence);

        Assert.Single(track.Transitions);
        Assert.Equal("c1", track.Transitions[0].From);
        Assert.Equal("c2", track.Transitions[0].To);
    }

    [Fact]
    public void Reconcile_MaintainsTransitionAtCut_WhenFromClipIsSplit()
    {
        var sequence = new Sequence();
        var track = new Track();
        var c1a = new Clip
        {
            Id = "c1a",
            MediaId = "media-1",
            Start = Time.Zero,
            SourceIn = Time.Zero,
            SourceOut = Time.FromSeconds(6)
        };
        var c1b = new Clip
        {
            Id = "c1b",
            MediaId = "media-1",
            Start = Time.FromSeconds(6),
            SourceIn = Time.FromSeconds(6),
            SourceOut = Time.FromSeconds(10)
        };
        var c2 = new Clip
        {
            Id = "c2",
            MediaId = "media-1",
            Start = Time.FromSeconds(10),
            SourceIn = Time.Zero,
            SourceOut = Time.FromSeconds(10)
        };
        track.Clips.Add(c1a);
        track.Clips.Add(c1b);
        track.Clips.Add(c2);

        var transition = new TransitionInstance
        {
            From = "c1a",
            To = "c2",
            Duration = Time.FromSeconds(2),
            Alignment = default
        };
        track.Transitions.Add(transition);
        sequence.VideoTracks.Add(track);

        TransitionOps.Reconcile(sequence);

        Assert.Single(track.Transitions);
        Assert.Equal("c1b", track.Transitions[0].From);
        Assert.Equal("c2", track.Transitions[0].To);
    }

    [Fact]
    public void Reconcile_RemovesTransition_WhenCutNoLongerExistsDueToGap()
    {
        var sequence = new Sequence();
        var track = new Track();
        var c1 = new Clip
        {
            Id = "c1",
            Start = Time.Zero,
            SourceIn = Time.Zero,
            SourceOut = Time.FromSeconds(10)
        };
        var c2 = new Clip
        {
            Id = "c2",
            Start = Time.FromSeconds(12),
            SourceIn = Time.Zero,
            SourceOut = Time.FromSeconds(10)
        };
        track.Clips.Add(c1);
        track.Clips.Add(c2);

        var transition = new TransitionInstance
        {
            From = "c1",
            To = "c2",
            Duration = Time.FromSeconds(2),
            Alignment = default
        };
        track.Transitions.Add(transition);
        sequence.VideoTracks.Add(track);

        TransitionOps.Reconcile(sequence);

        Assert.Empty(track.Transitions);
    }

    [Fact]
    public void Reconcile_ReadjustsTransitionDuration_WhenClipIsShortened()
    {
        var sequence = new Sequence();
        var track = new Track();
        var c1 = new Clip
        {
            Id = "c1",
            Start = Time.Zero,
            SourceIn = Time.Zero,
            SourceOut = Time.FromSeconds(2)
        };
        var c2 = new Clip
        {
            Id = "c2",
            Start = Time.FromSeconds(2),
            SourceIn = Time.Zero,
            SourceOut = Time.FromSeconds(10)
        };
        track.Clips.Add(c1);
        track.Clips.Add(c2);

        var transition = new TransitionInstance
        {
            From = "c1",
            To = "c2",
            Duration = Time.FromSeconds(6),
            Alignment = default
        };
        track.Transitions.Add(transition);
        sequence.VideoTracks.Add(track);

        TransitionOps.Reconcile(sequence);

        Assert.Single(track.Transitions);
        Assert.Equal(Time.FromSeconds(2), track.Transitions[0].Duration);
    }

    [Fact]
    public void Reconcile_RemovesTransition_WhenReadjustedDurationBecomesNonPositive()
    {
        var sequence = new Sequence();
        var track = new Track();
        var c1 = new Clip
        {
            Id = "c1",
            Start = Time.Zero,
            SourceIn = Time.Zero,
            SourceOut = Time.Zero
        };
        var c2 = new Clip
        {
            Id = "c2",
            Start = Time.Zero,
            SourceIn = Time.Zero,
            SourceOut = Time.FromSeconds(10)
        };
        track.Clips.Add(c1);
        track.Clips.Add(c2);

        var transition = new TransitionInstance
        {
            From = "c1",
            To = "c2",
            Duration = Time.FromSeconds(2),
            Alignment = default
        };
        track.Transitions.Add(transition);
        sequence.VideoTracks.Add(track);

        TransitionOps.Reconcile(sequence);

        Assert.Empty(track.Transitions);
    }
}
