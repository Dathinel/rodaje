using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Tests.Editing.Trimming;
using Rodaje.Core.Timing;
using static Rodaje.Core.Tests.Editing.Trimming.TrimFixture;

namespace Rodaje.Core.Tests.Editing.Speed;

public sealed class FrameSamplingEditsTests
{
    [Fact]
    public void SetFrameSampling_creates_speed_at_one_times_and_applies_to_linked_video_ignoring_audio()
    {
        var f = new TrimFixture()
            .Add("v1", "c1", "m1", 0, 50, 0, link: "L1")
            .Add("v2", "c2", "m2", 0, 50, 0, link: "L1")
            .Add("a1", "ca1", "m1", 0, 50, 0, link: "L1");

        TimelineEdits.SetFrameSampling(f.Ctx, ["c1"], FrameSampling.OpticalFlow);
        f.AssertValid();

        Clip c1 = f.Clip("c1");
        Clip c2 = f.Clip("c2");
        Clip ca1 = f.Clip("ca1");

        Assert.NotNull(c1.Speed);
        Assert.True(c1.Speed.IsIdentity);
        Assert.Equal(FrameSampling.OpticalFlow, c1.Speed.FrameSampling);
        Assert.Equal(F(50), c1.Duration);

        Assert.NotNull(c2.Speed);
        Assert.True(c2.Speed.IsIdentity);
        Assert.Equal(FrameSampling.OpticalFlow, c2.Speed.FrameSampling);
        Assert.Equal(F(50), c2.Duration);

        Assert.Null(ca1.Speed);

        TimelineEdits.SetFrameSampling(f.Ctx, ["c1"], FrameSampling.Nearest);
        f.AssertValid();

        Assert.Null(f.Clip("c1").Speed);
        Assert.Null(f.Clip("c2").Speed);
        Assert.Null(f.Clip("ca1").Speed);
    }

    [Fact]
    public void SetFrameSampling_ignores_frozen_stills_titles_and_compounds()
    {
        var f = new TrimFixture()
            .Add("v1", "frozen", "m1", 0, 50, 0)
            .Add("v1", "normal", "m1", 0, 50, 50)
            .AddTitle("v2", "title", 0, 50, 0);

        f.Clip("frozen").Speed = new ClipSpeed { Freeze = true };
        f.Clip("frozen").Duration = F(50);
        f.Clip("frozen").SourceOut = f.Clip("frozen").SourceIn + Time.FromTicks(1);
        f.Project.Media.Add(new MediaRef { Id = "still", Name = "foto.png", IsStillImage = true, HasVideo = true, Duration = Time.Zero });
        f.Add("v2", "stillClip", "still", 0, 50, 50);

        f.Project.Sequences.Add(new Sequence { Id = "nestedSeq", Name = "Anidada", FrameRate = Rational.Fps25 });
        f.Track("v2").Clips.Add(new Clip
        {
            Id = "compound",
            SequenceId = "nestedSeq",
            SourceIn = Time.Zero,
            SourceOut = F(50),
            Start = F(100),
        });

        TimelineEdits.SetFrameSampling(f.Ctx, ["frozen", "stillClip", "title", "compound", "normal"], FrameSampling.Blend);
        f.AssertValid();

        Assert.True(f.Clip("frozen").Speed!.Freeze);
        Assert.Equal(FrameSampling.Nearest, f.Clip("frozen").Speed!.FrameSampling);
        Assert.Null(f.Clip("stillClip").Speed);
        Assert.Null(f.Clip("title").Speed);
        Assert.Null(f.Clip("compound").Speed);

        Assert.NotNull(f.Clip("normal").Speed);
        Assert.Equal(FrameSampling.Blend, f.Clip("normal").Speed!.FrameSampling);
    }

    [Fact]
    public void Edits_SetFrameSampling_creates_command_named_Cuadros_intermedios()
    {
        var f = new TrimFixture()
            .Add("v1", "c1", "m1", 0, 50, 0)
            .Add("v1", "c2", "m1", 0, 50, 50);

        SequenceEditCommand command = Edits.SetFrameSampling(f.Sequence.Id, ["c1", "c2"], FrameSampling.OpticalFlow);
        Assert.Equal("Cuadros intermedios", command.Description);

        command.Apply(f.Project);
        f.AssertValid();

        Assert.Equal(FrameSampling.OpticalFlow, f.Clip("c1").Speed!.FrameSampling);
        Assert.Equal(FrameSampling.OpticalFlow, f.Clip("c2").Speed!.FrameSampling);
    }
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
            .Add("v2", "t2", "m2", 0, 100, 50, link: "T")
            .Add("a1", "ta", "m2", 0, 100, 50, link: "T");
        f.Clip("s").Speed = new ClipSpeed { FrameSampling = sampling };
        f.Clip("s").Duration = F(50);

        ClipAttributeEdits.Paste(f.Ctx, ["t"], ClipAttributes.Capture(f.Sequence, "s"), ClipAttributeKinds.Speed);

        f.AssertValid();
        foreach (string id in new[] { "t", "t2" })
        {
            Clip clip = f.Clip(id);
            Assert.NotNull(clip.Speed);
            Assert.True(clip.Speed.IsIdentity);
            Assert.Equal(sampling, clip.Speed.FrameSampling);
            Assert.Equal(F(100), clip.Duration);
        }

        Assert.Null(f.Clip("ta").Speed);
    }

    [Fact]
    public void Copying_attributes_from_linked_audio_preserves_video_frame_sampling()
    {
        var f = new TrimFixture()
            .Add("v1", "s", "m1", 0, 50, 0, link: "S")
            .Add("a1", "sa", "m1", 0, 50, 0, link: "S")
            .Add("v1", "target", "m2", 0, 100, 50);

        TimelineEdits.SetFrameSampling(f.Ctx, ["s"], FrameSampling.OpticalFlow);
        Assert.Null(f.Clip("sa").Speed);
        Assert.NotNull(f.Clip("s").Speed);

        ClipAttributes attrs = ClipAttributes.Capture(f.Sequence, "sa");
        Assert.NotNull(attrs.Speed);
        Assert.Equal(FrameSampling.OpticalFlow, attrs.Speed.FrameSampling);

        ClipAttributeEdits.Paste(f.Ctx, ["target"], attrs, ClipAttributeKinds.Speed);
        f.AssertValid();

        Clip target = f.Clip("target");
        Assert.NotNull(target.Speed);
        Assert.Equal(FrameSampling.OpticalFlow, target.Speed.FrameSampling);
    }

    [Fact]
    public void Pasting_one_times_nearest_clears_speed_on_target_with_optical_flow()
    {
        var f = new TrimFixture()
            .Add("v1", "source", "m1", 0, 50, 0)
            .Add("v1", "target", "m2", 0, 100, 50);

        TimelineEdits.SetFrameSampling(f.Ctx, ["target"], FrameSampling.OpticalFlow);
        Assert.NotNull(f.Clip("target").Speed);
        Assert.Null(f.Clip("source").Speed);

        ClipAttributeEdits.Paste(f.Ctx, ["target"], ClipAttributes.Capture(f.Sequence, "source"), ClipAttributeKinds.Speed);
        f.AssertValid();

        Assert.Null(f.Clip("target").Speed);
    }

    [Fact]
    public void Pasting_speed_does_not_set_frame_sampling_on_audio_or_still_images()
    {
        var f = new TrimFixture()
            .Add("v1", "s", "m1", 0, 50, 0)
            .Add("v1", "tv", "m2", 0, 100, 50, link: "L")
            .Add("a1", "ta", "m2", 0, 100, 50, link: "L");

        f.Project.Media.Add(new MediaRef { Id = "still", Name = "foto.png", IsStillImage = true, HasVideo = true, Duration = Time.Zero });
        f.Add("v2", "stillClip", "still", 0, 100, 50);

        TimelineEdits.SetFrameSampling(f.Ctx, ["s"], FrameSampling.OpticalFlow);

        ClipAttributeEdits.Paste(f.Ctx, ["tv", "stillClip"], ClipAttributes.Capture(f.Sequence, "s"), ClipAttributeKinds.Speed);
        f.AssertValid();

        Assert.NotNull(f.Clip("tv").Speed);
        Assert.Equal(FrameSampling.OpticalFlow, f.Clip("tv").Speed!.FrameSampling);
        Assert.Null(f.Clip("ta").Speed);
        Assert.Null(f.Clip("stillClip").Speed);
    }

    [Fact]
    public void Pasting_speed_when_target_selection_is_linked_audio_clears_video_optical_flow()
    {
        var f = new TrimFixture()
            .Add("v1", "source", "m1", 0, 50, 0)
            .Add("v1", "tv", "m2", 0, 100, 50, link: "L")
            .Add("a1", "ta", "m2", 0, 100, 50, link: "L");

        TimelineEdits.SetFrameSampling(f.Ctx, ["tv"], FrameSampling.OpticalFlow);
        Assert.NotNull(f.Clip("tv").Speed);
        Assert.Null(f.Clip("ta").Speed);

        ClipAttributeEdits.Paste(f.Ctx, ["ta"], ClipAttributes.Capture(f.Sequence, "source"), ClipAttributeKinds.Speed);
        f.AssertValid();

        Assert.Null(f.Clip("tv").Speed);
        Assert.Null(f.Clip("ta").Speed);
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

    [Fact]
    public void FreezeFrame_always_uses_nearest_sampling_even_if_source_has_interpolated_sampling()
    {
        var f = new TrimFixture().Add("v1", "c", "m1", 0, 50, 0);
        TimelineEdits.SetFrameSampling(f.Ctx, ["c"], FrameSampling.OpticalFlow);

        string frozenId = TimelineEdits.FreezeFrame(f.Ctx, "c", F(20), F(10));

        f.AssertValid();
        Clip frozen = f.Clip(frozenId);
        Assert.NotNull(frozen.Speed);
        Assert.True(frozen.Speed.Freeze);
        Assert.Equal(FrameSampling.Nearest, frozen.Speed.FrameSampling);
    }

    [Fact]
    public void Splitting_at_one_times_with_interpolation_then_returning_to_nearest_clears_speed_and_allows_join()
    {
        var f = new TrimFixture().Add("v1", "c", "m1", 0, 100, 0);
        TimelineEdits.SetFrameSampling(f.Ctx, ["c"], FrameSampling.Blend);

        IReadOnlyList<string> rightIds = TimelineEdits.Split(f.Ctx, "c", F(50));
        string rightId = rightIds[0];

        TimelineEdits.SetFrameSampling(f.Ctx, ["c", rightId], FrameSampling.Nearest);

        f.AssertValid();
        Clip left = f.Clip("c");
        Clip right = f.Clip(rightId);

        Assert.Null(left.Speed);
        Assert.Null(right.Speed);
        Assert.NotNull(left.TimeMap.Join(right.TimeMap));
    }

    [Fact]
    public void Trimming_in_at_one_times_with_interpolation_then_returning_to_nearest_clears_speed()
    {
        var f = new TrimFixture().Add("v1", "c", "m1", 0, 100, 0);
        TimelineEdits.SetFrameSampling(f.Ctx, ["c"], FrameSampling.Blend);

        TimelineEdits.Trim(f.Ctx, "c", TrimEdge.In, F(20));
        TimelineEdits.SetFrameSampling(f.Ctx, ["c"], FrameSampling.Nearest);

        f.AssertValid();
        Clip clip = f.Clip("c");
        Assert.Null(clip.Speed);
    }
}
