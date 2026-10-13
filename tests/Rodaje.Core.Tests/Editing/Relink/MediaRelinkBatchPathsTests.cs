using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using CoreTime = Rodaje.Core.Timing.Time;

namespace Rodaje.Core.Tests.Editing.Relink;

/// <summary>
/// El comando de volver a enlazar no deja un archivo como el de dos medios, y las rutas se comparan después de
/// normalizarlas (tarea 71).
/// </summary>
public class MediaRelinkBatchPathsTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "rodaje-reenlazar");

    private static string At(params string[] parts) => Path.Combine([Root, .. parts]);

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

    private static string[] PathsOf(Project project) => [.. project.Media.Select(m => m.Path)];

    [Fact]
    public void Two_equal_destinations_in_the_batch_are_rejected_without_changes()
    {
        Project project = ProjectWith(Media("a", At("viejo", "a.mp4")), Media("b", At("viejo", "b.mp4")));
        string[] before = PathsOf(project);

        var ex = Assert.Throws<EditException>(() => MediaRelinkEdits.Relink(project,
        [
            new MediaRelinkTarget("a", At("nuevo", "toma.mp4")),
            new MediaRelinkTarget("b", At("nuevo", "TOMA.MP4"), Probe() with { Width = 1280 }),
        ]));

        Assert.Contains("dos medios", ex.Message, StringComparison.Ordinal);
        Assert.Equal(before, PathsOf(project));
        Assert.Equal(1920, project.GetMedia("b").Width);
    }

    [Fact]
    public void A_destination_equal_to_a_media_outside_the_batch_is_rejected_without_changes()
    {
        Project project = ProjectWith(Media("a", At("viejo", "a.mp4")), Media("b", At("material", "b.mp4")));
        string[] before = PathsOf(project);

        var ex = Assert.Throws<EditException>(() => MediaRelinkEdits.Relink(project,
            [new MediaRelinkTarget("a", At("Material", "B.mp4"))]));

        Assert.Contains("«b»", ex.Message, StringComparison.Ordinal);
        Assert.Equal(before, PathsOf(project));
    }

    [Fact]
    public void A_batch_that_swaps_paths_is_valid()
    {
        string first = At("material", "1.mp4");
        string second = At("material", "2.mp4");
        Project project = ProjectWith(Media("a", first), Media("b", second));

        MediaRelinkEdits.Relink(project, [new MediaRelinkTarget("a", second), new MediaRelinkTarget("b", first)]);

        Assert.Equal([second, first], PathsOf(project));
    }

    [Fact]
    public void A_media_may_keep_its_own_path()
    {
        string path = At("material", "a.mp4");
        Project project = ProjectWith(Media("a", path), Media("b", At("material", "b.mp4")));

        MediaRelinkEdits.Relink(project, [new MediaRelinkTarget("a", path, Probe() with { Width = 1280 })]);

        Assert.Equal(1280, project.GetMedia("a").Width);
    }

    [Fact]
    public void Destinations_are_compared_after_resolving_dots()
    {
        Project project = ProjectWith(Media("a", At("viejo", "a.mp4")), Media("b", At("material", "b.mp4")));
        string dotted = Path.Combine(Root, "otra", "..", "material", ".", "b.mp4");

        Assert.Throws<EditException>(() => MediaRelinkEdits.Relink(project, [new MediaRelinkTarget("a", dotted)]));
        Assert.Throws<EditException>(() => MediaRelinkEdits.Relink(project,
            [new MediaRelinkTarget("a", At("nuevo", "x.mp4")), new MediaRelinkTarget("b", Path.Combine(Root, "nuevo", ".", "x.mp4"))]));
    }

    [Fact]
    public void CheckTaken_sees_the_same_file_through_dots_and_alternate_separators()
    {
        MediaRef a = Media("a", At("viejo", "a.mp4"));
        MediaRef b = Media("b", At("material", "b.mp4"));
        Project project = ProjectWith(a, b);
        string dotted = Path.Combine(Root, "otra", "..", "material", ".", "b.mp4");
        string alternate = At("material", "b.mp4").Replace(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        Assert.NotNull(MediaRelinkEdits.CheckTaken(project, a, dotted));
        Assert.NotNull(MediaRelinkEdits.CheckTaken(project, a, alternate));
        Assert.NotNull(MediaRelinkEdits.CheckTaken(project, b, Path.Combine(Root, "nuevo", ".", "x.mp4"),
            [new MediaRelinkTarget("a", At("nuevo", "x.mp4"))]));
        Assert.Null(MediaRelinkEdits.CheckTaken(project, a, At("material", "c.mp4")));
    }

    // Solo Windows: en Linux «\» no separa carpetas y «D:» no es una unidad. La integración continua de Linux excluye
    // esta categoría.
    [Fact]
    [Trait("Categoria", "SoloWindows")]
    public void Windows_paths_with_slashes_and_dots_are_the_same_file()
    {
        MediaRef a = Media("a", @"C:\viejo\a.mp4");
        MediaRef b = Media("b", @"D:\a\b.mp4");
        Project project = ProjectWith(a, b);

        Assert.NotNull(MediaRelinkEdits.CheckTaken(project, a, @"D:\a\.\b.mp4"));
        Assert.NotNull(MediaRelinkEdits.CheckTaken(project, a, "D:/a/b.mp4"));
        Assert.NotNull(MediaRelinkEdits.CheckTaken(project, a, @"D:\x\..\A\B.MP4"));
        Assert.Throws<EditException>(() => MediaRelinkEdits.Relink(project, [new MediaRelinkTarget("a", "D:/a/b.mp4")]));
        Assert.Equal(@"C:\viejo\a.mp4", project.GetMedia("a").Path);
    }

    [Fact]
    public void NormalizePath_keeps_a_path_it_cannot_resolve()
    {
        Assert.Equal("", MediaRelinkEdits.NormalizePath(""));
        Assert.Equal("a\0b", MediaRelinkEdits.NormalizePath("a\0b"));
    }

    [Fact]
    public void Search_sees_a_taken_path_written_with_dots()
    {
        // B ya tiene su archivo, pero escrito con un rodeo; A falta y en la carpeta solo está ese archivo con su nombre.
        string file = At("nuevo", "a.mp4");
        MediaRef a = Media("a", At("viejo", "a.mp4"));
        MediaRef b = Media("b", Path.Combine(Root, "otra", "..", "nuevo", ".", "a.mp4"));
        Project project = ProjectWith(a, b);
        var disk = new FakeRelinkFileSystem().Add(file, Probe());

        RelinkSearchResult result = MediaRelinkSearch.Search(project, [a], At("nuevo"), disk);

        Assert.Empty(result.Found);
    }
}
