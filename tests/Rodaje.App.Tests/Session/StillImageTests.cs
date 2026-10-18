using Rodaje.App.Services;
using Rodaje.App.Tests.Support;
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
        Clip clip = Assert.Single(session.Sequence.VideoTracks[0].Clips);
        Assert.Equal(Time.FromSeconds(5), clip.Duration);
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
    });
}
