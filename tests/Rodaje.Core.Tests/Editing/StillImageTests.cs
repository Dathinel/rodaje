using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Tests.Editing;

public sealed class StillImageTests
{
    [Fact]
    public void A_photo_starts_at_five_seconds_and_can_be_trimmed_past_both_source_edges_and_saved()
    {
        var media = new MediaRef { Id = "foto", Path = "foto.png", IsStillImage = true, HasVideo = true };
        var track = new Track { Id = "v", Kind = TrackKind.Video };
        var sequence = new Sequence { Id = "s", VideoTracks = [track] };
        var project = new Project { Id = "p", Media = [media], Sequences = [sequence] };
        var ctx = new EditContext(project, sequence, new SequentialIdGenerator());
        TimelineEdits.InsertMedia(ctx, media.Id, Time.Zero, media.InitialClipDuration, track.Id, Time.FromSeconds(10), PlacementMode.Strict);
        Clip clip = Assert.Single(track.Clips);
        Assert.Equal(Time.FromSeconds(5), clip.Duration);
        TimelineEdits.Trim(ctx, clip.Id, TrimEdge.Out, Time.FromSeconds(20));
        TimelineEdits.Trim(ctx, clip.Id, TrimEdge.In, Time.FromSeconds(-3));
        Assert.Equal(Time.FromSeconds(28), clip.Duration);
        Assert.Equal(Time.FromSeconds(-3), clip.SourceIn);
        Assert.Empty(ModelValidator.Validate(project));

        string directory = Directory.CreateTempSubdirectory("rodaje-foto-proyecto-").FullName;
        Project copy;
        try
        {
            string path = Path.Combine(directory, "foto.rodaje");
            ProjectSerializer.Save(project, path);
            copy = ProjectSerializer.Load(path);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        Clip saved = Assert.Single(copy.Sequences[0].VideoTracks[0].Clips);
        Assert.True(copy.Media[0].IsStillImage);
        Assert.Equal(clip.Duration, saved.Duration);
        Assert.Equal(clip.SourceIn, saved.SourceIn);
        Assert.Equal(clip.SourceOut, saved.SourceOut);
        Assert.Equal(clip.Start, saved.Start);
    }

    [Fact]
    public void Format_thirteen_migrates_without_turning_video_into_photos()
    {
        string path = Path.Combine(Serialization.ProjectSchemaTests.FindRepoRoot(), "tests", "Rodaje.Core.Tests", "Serialization", "ejemplos", "proyecto-v13.rodaje");
        Project copy = ProjectSerializer.Load(path);
        Assert.Equal(14, copy.SchemaVersion);
        Assert.False(copy.Media[0].IsStillImage);
        Assert.Equal(Time.FromSeconds(1), copy.Media[0].Duration);
    }
    private static (Project Project, Sequence Sequence, Track Track, Clip Clip, EditContext Context) Photo()
    {
        var media = new MediaRef { Id = "foto", Path = "foto.png", IsStillImage = true, HasVideo = true };
        var clip = new Clip { Id = "c", MediaId = media.Id, Start = Time.FromSeconds(10), SourceIn = Time.FromSeconds(-3), SourceOut = Time.FromSeconds(7) };
        var track = new Track { Id = "v", Kind = TrackKind.Video, Clips = [clip] };
        var sequence = new Sequence { Id = "s", VideoTracks = [track] };
        var project = new Project { Id = "p", Media = [media], Sequences = [sequence] };
        return (project, sequence, track, clip, new EditContext(project, sequence, new SequentialIdGenerator()));
    }

    [Theory]
    [InlineData(-3)]
    [InlineData(3)]
    public void Replace_video_with_photo_preserves_span_and_photo_with_video_starts_at_zero(int sourceIn)
    {
        var f = Photo();
        f.Clip.SourceIn = Time.FromSeconds(sourceIn);
        f.Clip.SourceOut = Time.FromSeconds(sourceIn + 10);
        var video = new MediaRef { Id = "video", Path = "v.mp4", HasVideo = true, Duration = Time.FromSeconds(30), FrameRate = Rational.Fps30 };
        f.Project.Media.Add(video);
        TimelineEdits.ReplaceMedia(f.Context, f.Clip.Id, video.Id);
        Assert.Equal(Time.Zero, f.Clip.SourceIn);
        Assert.Equal(Time.FromSeconds(10), f.Clip.SourceOut);
        TimelineEdits.ReplaceMedia(f.Context, f.Clip.Id, "foto");
        Assert.Equal(Time.FromSeconds(10), f.Clip.Duration);
        Assert.Equal("foto", f.Clip.MediaId);
        Assert.Empty(ModelValidator.Validate(f.Project));
    }

    [Theory]
    [InlineData(FourPointFit.Ask)]
    [InlineData(FourPointFit.Trim)]
    [InlineData(FourPointFit.FitToFill)]
    public void Four_points_with_a_photo_fill_the_gap_without_speed(FourPointFit fit)
    {
        var f = Photo();
        var request = new ThreePointRequest("foto", Time.FromSeconds(-2), Time.FromSeconds(3),
            Time.FromSeconds(30), Time.FromSeconds(42), Time.Zero, "v", null, PlacementMode.Overwrite, fit);
        ThreePointPlan plan = TimelineEdits.PlanThreePoint(f.Project, request);
        Assert.False(plan.NeedsDecision);
        Assert.Null(plan.Rate);
        Assert.Equal(Time.FromSeconds(10), plan.SourceOut);
        string id = Assert.Single(TimelineEdits.ThreePointEdit(f.Context, request, ThreePointAction.FitToFill));
        Clip result = f.Sequence.FindClip(id)!.Value.Clip;
        Assert.Equal(Time.FromSeconds(12), result.Duration);
        Assert.Null(result.Speed);
    }

    [Fact]
    public void Fit_a_photo_from_a_virtual_in_past_the_default_duration()
    {
        var f = Photo();
        var request = new ThreePointRequest("foto", Time.FromSeconds(20), null, Time.FromSeconds(30), Time.FromSeconds(42),
            Time.Zero, "v", null, PlacementMode.Overwrite);
        string id = Assert.Single(TimelineEdits.ThreePointEdit(f.Context, request, ThreePointAction.FitToFill));
        Clip result = f.Sequence.FindClip(id)!.Value.Clip;
        Assert.Equal(Time.FromSeconds(20), result.SourceIn);
        Assert.Equal(Time.FromSeconds(32), result.SourceOut);
        Assert.Null(result.Speed);
    }

    [Fact]
    public void Three_points_can_end_before_the_virtual_source_origin()
    {
        var f = Photo();
        var request = new ThreePointRequest("foto", null, Time.FromSeconds(2), Time.FromSeconds(30), Time.FromSeconds(42), Time.Zero,
            "v", null, PlacementMode.Overwrite);
        string id = Assert.Single(TimelineEdits.ThreePointEdit(f.Context, request));
        Assert.Equal(Time.FromSeconds(-10), f.Sequence.FindClip(id)!.Value.Clip.SourceIn);
    }

    [Fact]
    public void Resync_slip_allows_a_negative_photo_source_in()
    {
        var f = Photo();
        f.Clip.LinkId = "link";
        var other = new Clip { Id = "other", MediaId = "foto", LinkId = "link", Start = f.Clip.Start,
            SourceIn = Time.FromSeconds(2), SourceOut = Time.FromSeconds(12) };
        f.Sequence.VideoTracks.Add(new Track { Id = "v2", Kind = TrackKind.Video, Clips = [other] });
        LinkSync.Resync(f.Context, other.Id, ResyncMode.Slip);
        Assert.Equal(f.Clip.SourceIn, other.SourceIn);
        Assert.Empty(LinkSync.Find(f.Sequence));
    }

    [Fact]
    public void Relink_photo_retains_virtual_source_and_updates_metadata()
    {
        var f = Photo();
        MediaRef media = f.Project.Media[0];
        RelinkProbe probe = RelinkProbe.Of(media) with { Width = 1024, Height = 768 };
        MediaRelinkTarget target = MediaRelinkEdits.TargetFor(f.Project, media, "new.png", probe);
        MediaRelinkEdits.Relink(f.Project, [target]);
        Assert.Equal("new.png", media.Path);
        Assert.True(media.IsStillImage);
        Assert.Equal(1024, media.Width);
        Assert.Equal(Time.FromSeconds(-3), f.Clip.SourceIn);
        Assert.Empty(ModelValidator.Validate(f.Project));
    }

    [Theory]
    [InlineData(false, 13)]
    [InlineData(true, 14)]
    public void Save_uses_fourteen_only_when_the_library_contains_a_photo(bool photo, int version)
    {
        var f = Photo();
        f.Track.Clips.Clear();
        if (!photo)
        {
            f.Project.Media[0] = new MediaRef { Id = "video", Path = "v.mp4", HasVideo = true,
                Duration = Time.FromSeconds(30), FrameRate = Rational.Fps30 };
        }

        string json = ProjectSerializer.Serialize(f.Project);
        Assert.Equal(version, System.Text.Json.Nodes.JsonNode.Parse(json)!["schemaVersion"]!.GetValue<int>());
        Assert.Equal(14, f.Project.SchemaVersion);
        Assert.Empty(ModelValidator.Validate(ProjectSerializer.Deserialize(json)));
    }
}
