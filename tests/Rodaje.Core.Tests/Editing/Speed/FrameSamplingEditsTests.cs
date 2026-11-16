using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Tests.Editing.Trimming;
using Rodaje.Core.Timing;
using static Rodaje.Core.Tests.Editing.Trimming.TrimFixture;

namespace Rodaje.Core.Tests.Editing.Speed;

public sealed class FrameSamplingEditsTests
{
    [Theory]
    [InlineData(FrameSampling.Blend)]
    [InlineData(FrameSampling.OpticalFlow)]
    public void Returning_to_one_times_preserves_frame_sampling(FrameSampling sampling)
    {
        var f = new TrimFixture().Add("v1", "c", "m1", 0, 50, 0);
        TimelineEdits.SetSpeed(f.Ctx, "c", new Rational(1, 2));
        f.Clip("c").Speed!.FrameSampling = sampling;

        TimelineEdits.SetSpeed(f.Ctx, "c", new Rational(1, 1));

        f.AssertValid();
        Clip clip = f.Clip("c");
        Assert.NotNull(clip.Speed);
        Assert.True(clip.Speed.IsIdentity);
        Assert.Equal(sampling, clip.Speed.FrameSampling);
        Assert.Equal(Time.Zero, clip.Speed.Offset);
        Assert.Equal(clip.SourceDuration, clip.Duration);
    }

    [Theory]
    [InlineData(FrameSampling.Blend)]
    [InlineData(FrameSampling.OpticalFlow)]
    public void Pasting_from_one_times_carries_sampling_to_the_whole_link(FrameSampling sampling)
    {
        var f = new TrimFixture()
            .Add("v1", "s", "m1", 0, 50, 0)
            .Add("v1", "t", "m2", 0, 100, 50, link: "T")
            .Add("a1", "ta", "m2", 0, 100, 50, link: "T");
        f.Clip("s").Speed = new ClipSpeed { FrameSampling = sampling };
        f.Clip("s").Duration = F(50);

        ClipAttributeEdits.Paste(f.Ctx, ["t"], ClipAttributes.Capture(f.Sequence, "s"), ClipAttributeKinds.Speed);

        f.AssertValid();
        foreach (string id in new[] { "t", "ta" })
        {
            Clip clip = f.Clip(id);
            Assert.NotNull(clip.Speed);
            Assert.True(clip.Speed.IsIdentity);
            Assert.Equal(sampling, clip.Speed.FrameSampling);
            Assert.Equal(F(100), clip.Duration);
        }
    }

    [Theory]
    [InlineData(FrameSampling.Blend)]
    [InlineData(FrameSampling.OpticalFlow)]
    public void Pasting_onto_one_times_preserves_the_source_mode(FrameSampling sampling)
    {
        var f = new TrimFixture().Add("v1", "s", "m1", 0, 50, 0).Add("v2", "t", "m2", 0, 100, 0);
        TimelineEdits.SetSpeed(f.Ctx, "s", new Rational(1, 2));
        f.Clip("s").Speed!.FrameSampling = sampling;
        f.Clip("t").Speed = new ClipSpeed { FrameSampling = FrameSampling.Blend };
        f.Clip("t").Duration = F(100);

        ClipAttributeEdits.Paste(f.Ctx, ["t"], ClipAttributes.Capture(f.Sequence, "s"), ClipAttributeKinds.Speed);

        f.AssertValid();
        Assert.Equal(new Rational(1, 2), f.Clip("t").Speed!.Rate);
        Assert.Equal(sampling, f.Clip("t").Speed!.FrameSampling);
        Assert.Equal(F(200), f.Clip("t").Duration);
    }
}
