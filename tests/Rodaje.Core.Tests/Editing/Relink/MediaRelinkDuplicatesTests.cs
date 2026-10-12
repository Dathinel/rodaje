using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using CoreTime = Rodaje.Core.Timing.Time;

namespace Rodaje.Core.Tests.Editing.Relink;

/// <summary>Un archivo no se asigna a dos medios, tampoco al buscar por tandas como hace el diálogo (tarea 48).</summary>
public class MediaRelinkDuplicatesTests
{
    private static MediaRef Media(string id, string path) => new()
    {
        Id = id,
        Name = id,
        Path = path,
        Duration = CoreTime.FromSeconds(10),
        HasVideo = true,
        HasAudio = true,
        Width = 1920,
        Height = 1080,
        SampleRate = 48000,
        AudioChannels = 2,
    };

    private static RelinkProbe Probe() => RelinkProbe.Of(Media("x", "x"));

    private static Project ProjectWith(params MediaRef[] media)
    {
        var project = new Project { Id = "p", Name = "p" };
        project.Media.AddRange(media);
        return project;
    }

    [Fact]
    public void A_file_picked_by_hand_is_not_given_to_another_media_in_the_next_search()
    {
        // Faltan A y B, los dos 1080p de 10 s; en la carpeta nueva solo está el de B.
        MediaRef a = Media("a", @"C:\viejo\a.mp4");
        MediaRef b = Media("b", @"C:\viejo\b.mp4");
        Project project = ProjectWith(a, b);
        var disk = new FakeRelinkFileSystem().Add("/nuevo/b.mp4", Probe());
        var pending = new List<MediaRelinkTarget>();

        // Como el diálogo: se elige a mano el de B y luego se buscan en esa carpeta los que siguen faltando.
        pending.Add(MediaRelinkEdits.TargetFor(project, b, "/nuevo/b.mp4", Probe(), pending: pending));
        List<MediaRef> wanted = [.. new[] { a, b }.Where(m => pending.All(t => t.MediaId != m.Id))];
        RelinkSearchResult result = MediaRelinkSearch.Search(
            project, wanted, "/nuevo", disk, new RelinkSearchOptions { TakenPaths = [.. pending.Select(t => t.Path)] });

        Assert.Empty(result.Found);
        Assert.Empty(result.Ambiguous);
        Assert.Equal(["a"], result.NotFound);
    }

    [Fact]
    public void Taken_paths_are_compared_without_case()
    {
        MediaRef a = Media("a", @"C:\viejo\a.mp4");
        Project project = ProjectWith(a);
        var disk = new FakeRelinkFileSystem().Add("/nuevo/B.mp4", Probe());

        RelinkSearchResult result = MediaRelinkSearch.Search(
            project, [a], "/nuevo", disk, new RelinkSearchOptions { TakenPaths = ["/NUEVO/b.MP4"] });

        Assert.Equal(["a"], result.NotFound);
    }

    [Fact]
    public void Two_searches_in_a_row_do_not_repeat_a_file_already_found()
    {
        // La primera tanda encuentra el de A por nombre; en la segunda, para B, ese archivo ya está tomado.
        MediaRef a = Media("a", @"C:\viejo\a.mp4");
        MediaRef b = Media("b", @"C:\viejo\b.mp4");
        Project project = ProjectWith(a, b);
        var disk = new FakeRelinkFileSystem().Add("/nuevo/a.mp4", Probe());

        RelinkSearchResult first = MediaRelinkSearch.Search(project, [a], "/nuevo", disk);
        RelinkSearchResult second = MediaRelinkSearch.Search(
            project, [b], "/nuevo", disk, new RelinkSearchOptions { TakenPaths = [.. first.Found.Select(f => f.Path)] });

        Assert.Equal([new RelinkMatch("a", "/nuevo/a.mp4", RelinkMatchKind.SameName)], first.Found);
        Assert.Equal(["b"], second.NotFound);
    }

    [Fact]
    public void Picking_a_file_already_chosen_for_another_media_is_rejected()
    {
        MediaRef a = Media("a", @"C:\viejo\a.mp4");
        MediaRef b = Media("b", @"C:\viejo\b.mp4");
        Project project = ProjectWith(a, b);
        List<MediaRelinkTarget> pending = [new("b", @"D:\Nuevo\b.mp4")];

        var ex = Assert.Throws<EditException>(() => MediaRelinkEdits.TargetFor(project, a, @"d:\nuevo\B.MP4", Probe(), pending: pending));
        Assert.Contains("«b»", ex.Message, StringComparison.Ordinal);

        // Volver a elegir para B el que ya tenía elegido sí vale.
        Assert.Equal(@"d:\nuevo\B.MP4", MediaRelinkEdits.TargetFor(project, b, @"d:\nuevo\B.MP4", Probe(), pending: pending).Path);
    }

    [Fact]
    public void Picking_the_file_of_another_media_of_the_project_is_rejected()
    {
        MediaRef a = Media("a", @"C:\viejo\a.mp4");
        MediaRef c = Media("c", @"D:\Material\c.mp4");
        Project project = ProjectWith(a, c);

        Assert.NotNull(MediaRelinkEdits.CheckTaken(project, a, @"d:\material\C.mp4"));
        Assert.Throws<EditException>(() => MediaRelinkEdits.TargetFor(project, a, @"d:\material\C.mp4", Probe()));
        Assert.Null(MediaRelinkEdits.CheckTaken(project, a, @"D:\Material\otro.mp4"));
    }

    [Fact]
    public void The_old_path_of_a_media_that_is_being_relinked_elsewhere_is_free()
    {
        // C se enlaza con otro archivo en el mismo paso: su ruta de antes queda libre para A.
        MediaRef a = Media("a", @"C:\viejo\a.mp4");
        MediaRef c = Media("c", @"D:\Material\c.mp4");
        Project project = ProjectWith(a, c);

        Assert.Null(MediaRelinkEdits.CheckTaken(project, a, @"D:\Material\c.mp4", [new MediaRelinkTarget("c", @"E:\c.mp4")]));
    }
}
