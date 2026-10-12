using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Core.Tests.Commands;
using CoreTime = Rodaje.Core.Timing.Time;

namespace Rodaje.Core.Tests.Editing.Relink;

/// <summary>Volver a enlazar cambia solo los medios, en un paso del historial; clips, efectos y keyframes siguen igual.</summary>
public class MediaRelinkEditsTests
{
    private static string Clips(Project project) =>
        ProjectSerializer.Serialize(new Project { Id = project.Id, Name = project.Name, Sequences = project.Clone().Sequences });

    [Fact]
    public void Finds_the_media_whose_file_is_not_there()
    {
        Project project = CommandFixtures.Create();
        project.Media.Add(new MediaRef { Id = "media-2", Name = "b", Path = "/si/b.mp4", Duration = CoreTime.FromSeconds(1), HasVideo = true });

        Assert.Equal(["media-1"], MediaRelinkEdits.FindMissing(project, p => p == "/si/b.mp4").Select(m => m.Id));
        Assert.True(MediaRelinkEdits.CountClips(project, "media-1") > 0);
        Assert.Equal(0, MediaRelinkEdits.CountClips(project, "media-2"));
    }

    [Fact]
    public void One_undo_step_changes_only_the_paths()
    {
        Project project = CommandFixtures.Create();
        project.Media.Add(new MediaRef { Id = "media-2", Name = "b", Path = "b.mp4", Duration = CoreTime.FromSeconds(1), HasVideo = true });
        string clipsBefore = Clips(project);
        string before = ProjectSerializer.Serialize(project);
        var history = new UndoStack(project);

        history.Execute(new RelinkMediaCommand([new MediaRelinkTarget("media-1", "/nuevo/toma.mp4"), new MediaRelinkTarget("media-2", "/nuevo/b.mp4")]));

        Assert.Equal("/nuevo/toma.mp4", project.GetMedia("media-1").Path);
        Assert.Equal("/nuevo/b.mp4", project.GetMedia("media-2").Path);
        Assert.Equal("toma.mp4", project.GetMedia("media-1").Name);
        Assert.Equal(clipsBefore, Clips(project));
        Assert.Equal("Volver a enlazar 2 medios", history.UndoDescription);

        history.Undo();
        Assert.Equal(before, ProjectSerializer.Serialize(project));
        history.Redo();
        Assert.Equal("/nuevo/b.mp4", project.GetMedia("media-2").Path);
    }

    [Fact]
    public void A_different_file_chosen_by_hand_updates_the_media_data()
    {
        Project project = CommandFixtures.Create();
        MediaRef media = project.GetMedia("media-1");
        RelinkProbe longer = RelinkProbe.Of(media) with { Duration = CoreTime.FromSeconds(90), Width = 3840, Height = 2160 };

        MediaRelinkTarget target = MediaRelinkEdits.TargetFor(project, media, "/otro.mp4", longer);
        new RelinkMediaCommand([target]).Apply(project);

        Assert.Equal(CoreTime.FromSeconds(90), media.Duration);
        Assert.Equal(3840, media.Width);
        Assert.Equal("/otro.mp4", media.Path);
    }

    [Fact]
    public void The_same_file_chosen_by_hand_only_changes_the_path()
    {
        Project project = CommandFixtures.Create();
        MediaRef media = project.GetMedia("media-1");
        RelinkProbe almost = RelinkProbe.Of(media) with { Duration = media.Duration - CoreTime.FromSeconds(0.01) };

        Assert.Null(MediaRelinkEdits.TargetFor(project, media, "/igual.mp4", almost).Probe);
    }

    [Fact]
    public void Refuses_a_file_that_cannot_take_the_place_of_the_media()
    {
        Project project = CommandFixtures.Create();
        MediaRef media = project.GetMedia("media-1");
        RelinkProbe same = RelinkProbe.Of(media);

        Assert.Contains("no tiene imagen", MediaRelinkEdits.CheckReplacement(project, media, same with { HasVideo = false }), StringComparison.Ordinal);
        Assert.Contains("no tiene sonido", MediaRelinkEdits.CheckReplacement(project, media, same with { HasAudio = false }), StringComparison.Ordinal);
        Assert.Contains("usan hasta", MediaRelinkEdits.CheckReplacement(project, media, same with { Duration = CoreTime.FromSeconds(3) }), StringComparison.Ordinal);
        Assert.Null(MediaRelinkEdits.CheckReplacement(project, media, same));
        Assert.Throws<EditException>(() => MediaRelinkEdits.TargetFor(project, media, "/x.mp4", same with { Duration = CoreTime.Zero }));
    }

    [Fact]
    public void Fails_without_changes_on_unknown_or_repeated_media_or_an_invalid_result()
    {
        Project project = CommandFixtures.Create();
        string before = ProjectSerializer.Serialize(project);

        Assert.Throws<EditException>(() => new RelinkMediaCommand([new MediaRelinkTarget("media-1", "/a.mp4"), new MediaRelinkTarget("nada", "/b.mp4")]).Apply(project));
        Assert.Throws<EditException>(() => new RelinkMediaCommand([new MediaRelinkTarget("media-1", "/a.mp4"), new MediaRelinkTarget("media-1", "/b.mp4")]).Apply(project));
        Assert.Throws<EditException>(() => new RelinkMediaCommand([new MediaRelinkTarget("media-1", " ")]).Apply(project));
        RelinkProbe tooShort = RelinkProbe.Of(project.GetMedia("media-1")) with { Duration = CoreTime.FromSeconds(1) };
        Assert.Throws<EditException>(() => new RelinkMediaCommand([new MediaRelinkTarget("media-1", "/a.mp4", tooShort)]).Apply(project));
        Assert.Equal(before, ProjectSerializer.Serialize(project));
        Assert.Throws<ArgumentException>(() => new RelinkMediaCommand([]));
    }

    [Fact]
    public void The_search_result_becomes_targets_without_data_changes()
    {
        var result = new RelinkSearchResult([new RelinkMatch("media-1", "/n/toma.mp4", RelinkMatchKind.Renamed)], new Dictionary<string, IReadOnlyList<string>>(), [], 1, false);

        Assert.Equal([new MediaRelinkTarget("media-1", "/n/toma.mp4")], result.Targets);
    }
}
