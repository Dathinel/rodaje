using System.Collections.Generic;
using System.Linq;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Xunit;

namespace Rodaje.Core.Tests.Caza;

public class TimelineEditsCazaTests
{
    private const string Seq = "seq-1";

    private static Time S(double seconds) => Time.FromSeconds(seconds);

    private static Time? M(double? seconds) => seconds is { } s ? S(s) : null;

    private static Project Fixture()
    {
        static Clip C(string id, double start, double sourceIn, double length, string? link = null) =>
            new() { Id = id, MediaId = "m", Start = S(start), SourceIn = S(sourceIn), SourceOut = S(sourceIn + length), LinkId = link };
        return new Project
        {
            Id = "p",
            Media = [new MediaRef { Id = "m", Path = "m.mp4", Name = "toma", Duration = S(60), HasVideo = true, HasAudio = true, FrameRate = Rational.Fps30, Width = 1920, Height = 1080, SampleRate = 48000, AudioChannels = 2 }],
            Sequences =
            [
                new Sequence
                {
                    Id = Seq,
                    VideoTracks =
                    [
                        new Track { Id = "v1", Kind = TrackKind.Video, Name = "Video 1", Clips = [C("a", 0, 0, 10, "la"), C("b", 10, 20, 10, "lb")] },
                        new Track { Id = "v2", Kind = TrackKind.Video, Name = "Video 2", Clips = [C("t", 12, 40, 2)] },
                    ],
                    AudioTracks = [new Track { Id = "a1", Kind = TrackKind.Audio, Name = "Audio 1", Clips = [C("a2", 0, 0, 10, "la"), C("b2", 10, 20, 10, "lb")] }],
                },
            ],
        };
    }

    private static EditContext Context(Project project) =>
        new(project, project.Sequences[0], new SequentialIdGenerator());

    private static ThreePointRequest Request(
        double? sIn,
        double? sOut,
        double? qIn,
        double? qOut,
        PlacementMode mode = PlacementMode.Overwrite,
        FourPointFit fit = FourPointFit.Ask,
        double playhead = 1,
        string? video = "v1",
        string? audio = "a1",
        RippleScope scope = RippleScope.Track,
        double? sourcePlayhead = null) =>
        new("m", M(sIn), M(sOut), M(qIn), M(qOut), S(playhead), video, audio, mode, fit, scope, M(sourcePlayhead));

    [Fact]
    public void PlanThreePoint_WithoutSourceIn_UsesSourceStart()
    {
        Project project = Fixture();
        ThreePointRequest request = Request(null, null, 2, 6);
        ThreePointPlan plan = TimelineEdits.PlanThreePoint(project, request);

        Assert.Equal(Time.Zero, plan.SourceIn);
        Assert.Equal(S(4), plan.SourceOut);
        Assert.Equal(S(2), plan.At);
        Assert.Equal(S(4), plan.Duration);
    }

    [Fact]
    public void PlanThreePoint_WithoutSourceOut_CalculatesSourceEnd()
    {
        Project project = Fixture();
        ThreePointRequest request = Request(5, null, 2, 6);
        ThreePointPlan plan = TimelineEdits.PlanThreePoint(project, request);

        Assert.Equal(S(5), plan.SourceIn);
        Assert.Equal(S(9), plan.SourceOut);
        Assert.Equal(S(2), plan.At);
        Assert.Equal(S(4), plan.Duration);
    }

    [Fact]
    public void PlanThreePoint_WithoutSequenceIn_PlacesAtPlayhead()
    {
        Project project = Fixture();
        ThreePointRequest request = Request(5, 8, null, null, playhead: 3);
        ThreePointPlan plan = TimelineEdits.PlanThreePoint(project, request);

        Assert.Equal(S(5), plan.SourceIn);
        Assert.Equal(S(8), plan.SourceOut);
        Assert.Equal(S(3), plan.At);
        Assert.Equal(S(3), plan.Duration);
    }

    [Fact]
    public void PlanThreePoint_WithOnlySequenceOut_EndsClipAtSequenceOut()
    {
        Project project = Fixture();
        ThreePointRequest request = Request(5, 8, null, 10);
        ThreePointPlan plan = TimelineEdits.PlanThreePoint(project, request);

        Assert.Equal(S(7), plan.At);
        Assert.Equal(S(3), plan.Duration);
        Assert.Equal(S(10), plan.At + plan.Duration);
    }

    [Fact]
    public void PlanThreePoint_FourPointsWithDifferentDurations_FitToFillAdjustsSpeed()
    {
        Project project = Fixture();
        ThreePointRequest request = Request(0, 4, 2, 4, fit: FourPointFit.FitToFill);
        ThreePointPlan plan = TimelineEdits.PlanThreePoint(project, request);

        Assert.Equal(new Rational(2, 1), plan.Rate);
        Assert.Equal(S(2), plan.Duration);
        Assert.False(plan.NeedsDecision);
    }

    [Fact]
    public void PlanThreePoint_FourPointsWithDifferentDurations_TrimTrimsSource()
    {
        Project project = Fixture();
        ThreePointRequest request = Request(0, 4, 2, 4, fit: FourPointFit.Trim);
        ThreePointPlan plan = TimelineEdits.PlanThreePoint(project, request);

        Assert.Equal(S(0), plan.SourceIn);
        Assert.Equal(S(2), plan.SourceOut);
        Assert.Null(plan.Rate);
        Assert.False(plan.NeedsDecision);
    }

    [Fact]
    public void PlanThreePoint_FourPointsWithDifferentDurations_AskRequiresDecision()
    {
        Project project = Fixture();
        ThreePointRequest request = Request(0, 4, 2, 4, fit: FourPointFit.Ask);
        ThreePointPlan plan = TimelineEdits.PlanThreePoint(project, request);

        Assert.True(plan.NeedsDecision);
    }

    [Fact]
    public void ThreePointEdit_Overwrite_PlacesClipsOnVideoAndAudioTracksLinked()
    {
        Project project = Fixture();
        EditContext ctx = Context(project);
        ThreePointRequest request = Request(30, 33, 4, null, mode: PlacementMode.Overwrite, video: "v1", audio: "a1");
        IReadOnlyList<string> created = TimelineEdits.ThreePointEdit(ctx, request);

        Assert.Equal(2, created.Count);
        Sequence seq = project.Sequences[0];
        (Track videoTrack, Clip videoClip) = seq.GetClip(created[0]);
        (Track audioTrack, Clip audioClip) = seq.GetClip(created[1]);
        Assert.Equal("v1", videoTrack.Id);
        Assert.Equal("a1", audioTrack.Id);
        Assert.NotNull(videoClip.LinkId);
        Assert.Equal(videoClip.LinkId, audioClip.LinkId);
        Assert.Equal(S(4), videoClip.Start);
        Assert.Equal(S(7), videoClip.End);
        Assert.Equal(S(4), audioClip.Start);
        Assert.Equal(S(7), audioClip.End);
    }

    [Fact]
    public void ThreePointEdit_Insert_RipplesAccordingToRippleScope()
    {
        Project projectTrack = Fixture();
        EditContext ctxTrack = Context(projectTrack);
        ThreePointRequest reqTrack = Request(30, 33, 4, null, mode: PlacementMode.Insert, scope: RippleScope.Track, video: "v1", audio: "a1");
        TimelineEdits.ThreePointEdit(ctxTrack, reqTrack);
        Sequence seqTrack = projectTrack.Sequences[0];
        Assert.Equal(S(23), seqTrack.VideoTracks[0].End);
        Assert.Equal(S(23), seqTrack.AudioTracks[0].End);
        Assert.Equal(S(12), seqTrack.VideoTracks[1].Clips[0].Start);

        Project projectAll = Fixture();
        EditContext ctxAll = Context(projectAll);
        ThreePointRequest reqAll = Request(30, 33, 4, null, mode: PlacementMode.Insert, scope: RippleScope.AllTracks, video: "v1", audio: "a1");
        TimelineEdits.ThreePointEdit(ctxAll, reqAll);
        Sequence seqAll = projectAll.Sequences[0];
        Assert.Equal(S(23), seqAll.VideoTracks[0].End);
        Assert.Equal(S(23), seqAll.AudioTracks[0].End);
        Assert.Equal(S(15), seqAll.VideoTracks[1].Clips[0].Start);
    }

    [Fact]
    public void ThreePointEdit_ActionReplace_WithSequenceMarks_AlignsSourceIn()
    {
        Project project = Fixture();
        EditContext ctx = Context(project);
        ThreePointRequest request = Request(10, null, 2, 6, mode: PlacementMode.Overwrite, video: "v1", audio: null);
        IReadOnlyList<string> created = TimelineEdits.ThreePointEdit(ctx, request, ThreePointAction.Replace);
        Clip clip = project.Sequences[0].GetClip(Assert.Single(created)).Clip;

        Assert.Equal(S(2), clip.Start);
        Assert.Equal(S(6), clip.End);
        Assert.Equal(S(10), clip.SourceIn);
        Assert.Equal(S(14), clip.SourceOut);
    }

    [Fact]
    public void ThreePointEdit_ActionReplace_WithClipUnderPlayhead_AlignsSourcePlayhead()
    {
        Project project = Fixture();
        EditContext ctx = Context(project);
        ThreePointRequest request = new("m", null, null, null, null, S(4), "v1", null, PlacementMode.Overwrite, SourcePlayhead: S(14));
        IReadOnlyList<string> created = TimelineEdits.ThreePointEdit(ctx, request, ThreePointAction.Replace);
        Clip clip = project.Sequences[0].GetClip(Assert.Single(created)).Clip;

        Assert.Equal(S(0), clip.Start);
        Assert.Equal(S(10), clip.End);
        Assert.Equal(S(10), clip.SourceIn);
        Assert.Equal(S(20), clip.SourceOut);
    }

    [Fact]
    public void ThreePointEdit_ActionFitToFill_AdjustsSpeedToFillSpan()
    {
        Project project = Fixture();
        EditContext ctx = Context(project);
        ThreePointRequest request = Request(0, 8, 2, 6, mode: PlacementMode.Overwrite, video: "v1", audio: null);
        IReadOnlyList<string> created = TimelineEdits.ThreePointEdit(ctx, request, ThreePointAction.FitToFill);
        Clip clip = project.Sequences[0].GetClip(Assert.Single(created)).Clip;

        Assert.Equal(S(2), clip.Start);
        Assert.Equal(S(6), clip.End);
        Assert.NotNull(clip.Speed);
        Assert.Equal(new Rational(2, 1), clip.Speed.Rate);
    }

    [Fact]
    public void ThreePointEdit_ActionSuperimpose_PlacesVideoOnFirstFreeVideoTrackAbove()
    {
        Project project = Fixture();
        EditContext ctx = Context(project);
        ThreePointRequest request = Request(0, 4, 2, 6, mode: PlacementMode.Overwrite, video: "v1", audio: null);
        IReadOnlyList<string> created = TimelineEdits.ThreePointEdit(ctx, request, ThreePointAction.Superimpose);
        (Track track, Clip clip) = project.Sequences[0].GetClip(Assert.Single(created));

        Assert.Equal("v2", track.Id);
        Assert.Equal(S(2), clip.Start);
        Assert.Equal(S(6), clip.End);
    }

    [Fact]
    public void ThreePointEdit_ActionSuperimpose_CreatesNewVideoTrackWhenNoneFree()
    {
        Project project = Fixture();
        EditContext ctx = Context(project);
        ThreePointRequest request = Request(0, 4, 11, 15, mode: PlacementMode.Overwrite, video: "v1", audio: null);
        IReadOnlyList<string> created = TimelineEdits.ThreePointEdit(ctx, request, ThreePointAction.Superimpose);
        (Track track, Clip clip) = project.Sequences[0].GetClip(Assert.Single(created));

        Assert.Equal(3, project.Sequences[0].VideoTracks.Count);
        Assert.Equal(project.Sequences[0].VideoTracks[2].Id, track.Id);
        Assert.Equal(S(11), clip.Start);
        Assert.Equal(S(15), clip.End);
    }

    [Fact]
    public void ThreePointEdit_InsertWithTrackScope_RipplesLinkedClipsOnOtherTracks()
    {
        Project project = Fixture();
        EditContext ctx = Context(project);
        ThreePointRequest request = Request(30, 33, 4, null, mode: PlacementMode.Insert, scope: RippleScope.Track, video: "v1", audio: null);
        TimelineEdits.ThreePointEdit(ctx, request);
        Sequence seq = project.Sequences[0];
        Clip clipB = seq.VideoTracks[0].Clips.First(c => c.Id == "b");
        Clip clipB2 = seq.AudioTracks[0].Clips.First(c => c.Id == "b2");

        Assert.Equal(S(13), clipB.Start);
        Assert.Equal(S(13), clipB2.Start);
    }
}
