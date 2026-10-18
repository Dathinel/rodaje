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
}
