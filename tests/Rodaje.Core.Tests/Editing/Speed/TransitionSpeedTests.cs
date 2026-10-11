using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Tests.Editing.Trimming;
using Rodaje.Core.Timing;
using static Rodaje.Core.Tests.Editing.Trimming.TrimFixture;

namespace Rodaje.Core.Tests.Editing.Speed;

public sealed class TransitionSpeedTests
{
    public static TheoryData<string> Speeds => [.. SpeedSamples.Names];

    [Theory]
    [MemberData(nameof(Speeds))]
    public void Splitting_the_outgoing_clip_keeps_the_transition_on_its_cut(string speed)
    {
        var f = new TrimFixture();
        SpeedSamples.Add(f, "v1", "a", "m2", 0, 40, speed);
        f.Add("v1", "b", "m1", 0, 20, 40);
        f.Track("v1").Transitions.Add(new TransitionInstance { Id = "t", Type = BuiltInTransitions.Crossfade, From = "a", To = "b", Duration = F(4) });

        IReadOnlyList<string> ids = TimelineEdits.Split(f.Ctx, "a", F(17) + Time.FromTicks(5));

        TransitionInstance transition = Assert.Single(f.Track("v1").Transitions);
        Assert.Equal(ids[0], transition.From);
        Assert.Equal(F(4), transition.Duration);
        f.AssertValid();
    }

    [Fact]
    public void A_neighbour_with_another_speed_or_a_jump_in_the_offset_is_not_the_same_clip()
    {
        foreach (Action<Clip> spoil in (Action<Clip>[])
        [
            c => c.Speed!.Rate = new Rational(3, 1),
            c => c.Speed!.Offset += Time.FromTicks(1),
            c => c.Speed = null,
        ])
        {
            var f = new TrimFixture();
            SpeedSamples.Add(f, "v1", "a", "m2", 0, 40, "2×");
            f.Add("v1", "b", "m1", 0, 20, 40);
            f.Track("v1").Transitions.Add(new TransitionInstance { Id = "t", Type = BuiltInTransitions.Crossfade, From = "a", To = "b", Duration = F(4) });
            Clip right = TrackOps.SplitOff(f.Ctx, f.Clip("a"), F(20));
            TrackOps.AddClipSorted(f.Track("v1"), right);

            TransitionOps.Reconcile(f.Sequence);
            Assert.Equal(right.Id, Assert.Single(f.Track("v1").Transitions).From);

            f.Track("v1").Transitions[0].From = "a";
            spoil(right);
            TransitionOps.Reconcile(f.Sequence);
            Assert.Empty(f.Track("v1").Transitions);
        }
    }

    [Fact]
    public void Speeding_up_on_a_free_track_keeps_the_cut_and_shortens_a_transition_that_no_longer_fits()
    {
        var f = new TrimFixture().Add("v1", "a", "m1", 0, 20, 0).Add("v1", "b", "m2", 0, 40, 20);
        f.Track("v1").Transitions.Add(new TransitionInstance { Id = "t", Type = BuiltInTransitions.Crossfade, From = "a", To = "b", Duration = F(16) });

        TimelineEdits.SetSpeed(f.Ctx, "a", new Rational(2, 1));

        TransitionInstance transition = Assert.Single(f.Track("v1").Transitions);
        Assert.Equal(F(10), transition.Duration);
        Assert.Equal(F(10), f.Clip("b").Start);
        f.AssertValid();
    }

    [Fact]
    public void Slowing_down_on_a_free_track_keeps_the_cut_because_the_neighbour_is_pushed()
    {
        var f = new TrimFixture().Add("v1", "a", "m1", 0, 20, 0).Add("v1", "b", "m2", 0, 40, 20);
        f.Track("v1").Transitions.Add(new TransitionInstance { Id = "t", Type = BuiltInTransitions.Crossfade, From = "a", To = "b", Duration = F(8) });

        TimelineEdits.SetSpeed(f.Ctx, "a", new Rational(1, 2));

        Assert.Single(f.Track("v1").Transitions);
        Assert.Equal(F(40), f.Clip("b").Start);
        f.AssertValid();
    }
}
