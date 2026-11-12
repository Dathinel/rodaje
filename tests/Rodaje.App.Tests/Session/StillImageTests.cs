using Rodaje.App.Services;
using Rodaje.App.Services.Tracking;
using Rodaje.App.Tests.Support;
using Rodaje.App.Views.Analysis;
using Rodaje.App.Views.Library;
using Rodaje.App.Views.Timeline;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.TestMedia;

namespace Rodaje.App.Tests.Session;

public sealed class StillImageTests
{
    [FfmpegLibraryFact]
    public void Import_append_source_preview_and_drop_use_the_five_second_default() => UiThread.Run(async () =>
    {
        using var image = new StillImageFile();
        var playback = new FakePlayback();
        var session = new EditorSession(playback, new FfmpegMediaProber(), new FakeDialogs(), new SequentialIdGenerator());
        await session.OpenFilesAsync([image.Path]);
        MediaRef media = Assert.Single(session.Project.Media);
        Assert.True(media.IsStillImage);
        Assert.Equal("Imagen", MediaDetails.DurationText(media));
        Clip clip = Assert.Single(session.Sequence.VideoTracks[0].Clips);
        Assert.Equal(Time.FromSeconds(5), clip.Duration);
        await Assert.ThrowsAsync<NotSupportedException>(() => ClipVideoSource.AnalyzeColorAsync(session, clip, null, CancellationToken.None));
        await Assert.ThrowsAsync<NotSupportedException>(() => ClipVideoSource.AnalyzeMotionAsync(session, clip,
            new StabilizationStore(Path.GetTempPath()), null, CancellationToken.None));
        Assert.True(session.OpenSource(media.Id));
        var loaded = playback.Loads[^1];
        Assert.Equal(Time.FromSeconds(5), loaded.Project.GetSequence(loaded.SequenceId).Duration);
        session.ShowProgram();
        Track track = session.Sequence.VideoTracks[1];
        TrackRow row = TrackLayout.Build(session.Sequence, session.MainTrack).Find(track.Id)!;
        DropPlan plan = TimelinePlanning.PlanDrop(session.Project, session.Sequence, media, row, Time.Zero, false, new DragOptions(), null)!;
        Assert.Equal(Time.FromSeconds(5), plan.Duration);
        Assert.True(session.Execute(plan.Build()));
        Clip dropped = Assert.Single(track.Clips);
        Assert.Equal(Time.FromSeconds(5), dropped.Duration);
        var drag = new ClipDrag(session.Project, session.SequenceId, dropped.Id, DragOperation.TrimEnd, dropped.End,
            new DragOptions { Snapping = false }, null);
        DragPlan trim = drag.Update(session.Project, Time.FromSeconds(20), row);
        Assert.NotNull(trim.Build);
        Assert.True(session.Execute(trim.Build()));
        Assert.Equal(Time.FromSeconds(20), dropped.Duration);
        dropped.Start = Time.FromSeconds(10);
        var trimStart = new ClipDrag(session.Project, session.SequenceId, dropped.Id, DragOperation.TrimStart, dropped.Start,
            new DragOptions { Snapping = false }, null);
        DragPlan extended = trimStart.Update(session.Project, Time.FromSeconds(7), row);
        Assert.NotNull(extended.Build);
        Assert.True(session.Execute(extended.Build()));
        Assert.Equal(Time.FromSeconds(-3), dropped.SourceIn);
    });

    [FfmpegLibraryFact]
    public void Tracking_and_motion_reject_photos_before_reading_luma()
    {
        using var image = new StillImageFile();
        const string message = "El seguimiento necesita un video; esta es una imagen fija";
        Assert.Equal(message, Assert.Throws<NotSupportedException>(() => new FrameWalk(image.Path)).Message);
        string directory = Directory.CreateTempSubdirectory("rodaje-foto-motion-").FullName;
        try
        {
            Assert.Equal(message, Assert.Throws<NotSupportedException>(() => ClipVideoSource.Motion(image.Path, Time.Zero,
                Time.FromSeconds(5), new StabilizationStore(directory), null, CancellationToken.None)).Message);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
