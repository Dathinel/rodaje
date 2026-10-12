using Rodaje.App.Services.Relink;
using Rodaje.App.Tests.Support;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using CoreTime = Rodaje.Core.Timing.Time;

namespace Rodaje.App.Tests.Relink;

/// <summary>
/// El pegamento entre el diálogo, el servicio y la búsqueda: las rutas ocupadas llegan a la búsqueda y a la comprobación de
/// un archivo elegido a mano (tarea 71).
/// </summary>
public class MediaRelinkServiceTakenTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "rodaje-reenlazar-app");

    private static string At(params string[] parts) => Path.Combine([Root, .. parts]);

    /// <summary>Un disco en memoria: los archivos dados, todos con los datos de <see cref="Media"/>.</summary>
    private sealed class FakeFileSystem(params string[] paths) : IRelinkFileSystem
    {
        public List<string> Probed { get; } = [];

        public IEnumerable<RelinkFile> EnumerateFiles(string folder, CancellationToken cancellationToken) =>
            paths.Where(p => p.StartsWith(folder, StringComparison.Ordinal)).Select(p => new RelinkFile(p, 1000));

        public RelinkProbe? Probe(string path)
        {
            Probed.Add(path);
            return paths.Contains(path) ? RelinkProbe.Of(Media("x", path)) : null;
        }

        public string? Fingerprint(string path, int bytes) => path;
    }

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

    private static Project ProjectWith(params MediaRef[] media)
    {
        var project = new Project { Id = "p", Name = "p" };
        project.Media.AddRange(media);
        return project;
    }

    [Fact]
    public async Task Search_without_taken_finds_the_file()
    {
        MediaRef a = Media("a", At("viejo", "a.mp4"));
        var service = new MediaRelinkService(new FakeProber(), new FakeFileSystem(At("nuevo", "a.mp4")));

        RelinkSearchResult result = await service.SearchAsync(ProjectWith(a), [a], At("nuevo"));

        Assert.Equal([new MediaRelinkTarget("a", At("nuevo", "a.mp4"))], result.Targets);
    }

    [Fact]
    public async Task Search_does_not_give_a_taken_path_to_another_media()
    {
        MediaRef a = Media("a", At("viejo", "a.mp4"));
        string file = At("nuevo", "a.mp4");
        var service = new MediaRelinkService(new FakeProber(), new FakeFileSystem(file));

        RelinkSearchResult result = await service.SearchAsync(ProjectWith(a), [a], At("nuevo"), taken: [file]);

        Assert.Empty(result.Targets);
        Assert.Equal(["a"], result.NotFound);
    }

    [Fact]
    public async Task Search_keeps_the_taken_paths_of_its_options_and_adds_the_dialog_ones()
    {
        MediaRef a = Media("a", At("viejo", "a.mp4"));
        MediaRef b = Media("b", At("viejo", "b.mp4"));
        var service = new MediaRelinkService(new FakeProber(), new FakeFileSystem(At("nuevo", "a.mp4"), At("nuevo", "b.mp4")))
        {
            SearchOptions = new RelinkSearchOptions { TakenPaths = [At("nuevo", "a.mp4")], IncludeRenamed = false },
        };

        RelinkSearchResult result = await service.SearchAsync(ProjectWith(a, b), [a, b], At("nuevo"), taken: [At("nuevo", "b.mp4")]);

        Assert.Empty(result.Targets);
        Assert.Equal(["a", "b"], result.NotFound);
    }

    [Fact]
    public async Task Search_compares_taken_paths_after_resolving_dots_and_separators()
    {
        MediaRef a = Media("a", At("viejo", "a.mp4"));
        string file = At("nuevo", "a.mp4");
        string dotted = Path.Combine(Root, "otra", "..", "nuevo", ".", "a.mp4");
        string alternate = file.Replace(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var service = new MediaRelinkService(new FakeProber(), new FakeFileSystem(file));

        Assert.Empty((await service.SearchAsync(ProjectWith(a), [a], At("nuevo"), taken: [dotted])).Targets);
        Assert.Empty((await service.SearchAsync(ProjectWith(a), [a], At("nuevo"), taken: [alternate])).Targets);
    }

    [Fact]
    public async Task Search_does_not_give_the_file_of_a_linked_media_written_with_dots()
    {
        // B ya tiene su archivo, escrito con un rodeo; A falta y en la carpeta solo está ese archivo, con el nombre de A.
        string file = At("nuevo", "a.mp4");
        MediaRef a = Media("a", At("viejo", "a.mp4"));
        MediaRef b = Media("b", Path.Combine(Root, "otra", "..", "nuevo", ".", "a.mp4"));
        var service = new MediaRelinkService(new FakeProber(), new FakeFileSystem(file));

        RelinkSearchResult result = await service.SearchAsync(ProjectWith(a, b), [a], At("nuevo"));

        Assert.Empty(result.Targets);
    }

    [Fact]
    public async Task CheckFile_rejects_a_file_already_chosen_for_another_media_without_probing()
    {
        MediaRef a = Media("a", At("viejo", "a.mp4"));
        MediaRef b = Media("b", At("viejo", "b.mp4"));
        string file = At("nuevo", "toma.mp4");
        var prober = new FakeProber().Video(file);
        var service = new MediaRelinkService(prober, new FakeFileSystem(file));

        (MediaRelinkTarget? target, string? problem) = await service.CheckFileAsync(
            ProjectWith(a, b), a, Path.Combine(Root, "nuevo", ".", "toma.mp4"), [new MediaRelinkTarget("b", file)]);

        Assert.Null(target);
        Assert.Contains("«b»", problem, StringComparison.Ordinal);
        Assert.Equal(0, prober.Calls);
    }

    [Fact]
    public async Task CheckFile_accepts_a_file_whose_pending_entry_is_for_the_same_media()
    {
        MediaRef a = Media("a", At("viejo", "a.mp4"));
        string file = At("nuevo", "toma.mp4");
        var service = new MediaRelinkService(new FakeProber().Video(file, 10), new FakeFileSystem(file));
        Project project = ProjectWith(a);
        IReadOnlyList<MediaRelinkTarget> others =
            MediaRelinkService.PendingForOthers([new MediaRelinkTarget("a", file)], a.Id);

        (MediaRelinkTarget? target, string? problem) = await service.CheckFileAsync(project, a, file, others);

        Assert.Null(problem);
        Assert.Equal(file, target?.Path);
    }

    [Fact]
    public void TakenPaths_joins_pending_and_proposed()
    {
        IReadOnlyList<string> taken = MediaRelinkService.TakenPaths(
            [new MediaRelinkTarget("a", At("x", "a.mp4")), new MediaRelinkTarget("b", At("x", "b.mp4"))], [At("y", "c.mp4")]);

        Assert.Equal([At("x", "a.mp4"), At("x", "b.mp4"), At("y", "c.mp4")], taken);
    }

    [Fact]
    public void PendingForOthers_leaves_out_the_media_being_chosen()
    {
        var a = new MediaRelinkTarget("a", At("x", "a.mp4"));
        var b = new MediaRelinkTarget("b", At("x", "b.mp4"));

        Assert.Equal([b], MediaRelinkService.PendingForOthers([a, b], "a"));
        Assert.Equal([a, b], MediaRelinkService.PendingForOthers([a, b], "z"));
    }
}
