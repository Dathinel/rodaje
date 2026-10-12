using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using CoreTime = Rodaje.Core.Timing.Time;

namespace Rodaje.Core.Tests.Editing.Relink;

/// <summary>Buscar en una carpeta los archivos de los medios que faltan (función 32 de docs/pendientes.md).</summary>
public class MediaRelinkSearchTests
{
    private static MediaRef Media(string id, string path, double seconds = 10, int width = 1920, bool audio = true) => new()
    {
        Id = id,
        Name = id,
        Path = path,
        Duration = CoreTime.FromSeconds(seconds),
        HasVideo = true,
        HasAudio = audio,
        Width = width,
        Height = width * 9 / 16,
        SampleRate = audio ? 48000 : 0,
        AudioChannels = audio ? 2 : 0,
    };

    private static RelinkProbe Probe(double seconds = 10, int width = 1920, bool audio = true) =>
        RelinkProbe.Of(Media("x", "x", seconds, width, audio));

    private static (Project Project, List<MediaRef> Missing) ProjectWith(params MediaRef[] media)
    {
        var project = new Project { Id = "p", Name = "p" };
        project.Media.AddRange(media);
        return (project, [.. media]);
    }

    [Fact]
    public void Finds_moved_files_by_name_in_subfolders_and_ignores_case()
    {
        (Project project, List<MediaRef> missing) = ProjectWith(Media("a", @"C:\viejo\toma 01.mp4"), Media("b", @"C:\viejo\fotos\voz.MP4", 30));
        var disk = new FakeRelinkFileSystem()
            .Add("/nuevo/material/Toma 01.mp4", Probe())
            .Add("/nuevo/otra/voz.mp4", Probe(30));

        RelinkSearchResult result = MediaRelinkSearch.Search(project, missing, "/nuevo", disk);

        Assert.Equal(
            [new RelinkMatch("a", "/nuevo/material/Toma 01.mp4", RelinkMatchKind.SameName), new RelinkMatch("b", "/nuevo/otra/voz.mp4", RelinkMatchKind.SameName)],
            result.Found);
        Assert.Empty(result.Ambiguous);
        Assert.Empty(result.NotFound);
        Assert.Equal(2, result.FilesScanned);
        Assert.False(result.Truncated);
    }

    [Fact]
    public void A_file_with_the_same_name_but_other_duration_or_size_is_not_the_media()
    {
        (Project project, List<MediaRef> missing) = ProjectWith(Media("a", @"C:\viejo\toma.mp4"));
        var disk = new FakeRelinkFileSystem()
            .Add("/n/1/toma.mp4", Probe(seconds: 12))
            .Add("/n/2/toma.mp4", Probe(width: 1280))
            .Add("/n/3/toma.mp4", Probe(audio: false))
            .Add("/n/4/toma.mp4", null);

        RelinkSearchResult result = MediaRelinkSearch.Search(project, missing, "/n", disk);

        Assert.Empty(result.Found);
        Assert.Equal(["a"], result.NotFound);
    }

    [Fact]
    public void Duration_within_the_tolerance_still_matches()
    {
        (Project project, List<MediaRef> missing) = ProjectWith(Media("a", "toma.mp4"));
        var disk = new FakeRelinkFileSystem().Add("/n/toma.mp4", Probe(seconds: 10.03));

        Assert.Single(MediaRelinkSearch.Search(project, missing, "/n", disk).Found);
        Assert.Empty(MediaRelinkSearch.Search(project, missing, "/n", disk, new RelinkSearchOptions { DurationTolerance = CoreTime.Zero }).Found);
    }

    [Fact]
    public void Finds_a_renamed_file_with_the_same_extension_and_data()
    {
        (Project project, List<MediaRef> missing) = ProjectWith(Media("a", @"C:\viejo\toma.mp4", 42));
        var disk = new FakeRelinkFileSystem()
            .Add("/n/boda final.mp4", Probe(42))
            .Add("/n/otra.mov", Probe(42))
            .Add("/n/corta.mp4", Probe(5));

        RelinkSearchResult result = MediaRelinkSearch.Search(project, missing, "/n", disk);

        Assert.Equal([new RelinkMatch("a", "/n/boda final.mp4", RelinkMatchKind.Renamed)], result.Found);
        Assert.DoesNotContain("/n/otra.mov", disk.Probed);
        Assert.Empty(MediaRelinkSearch.Search(project, missing, "/n", disk, new RelinkSearchOptions { IncludeRenamed = false }).Found);
    }

    [Fact]
    public void Prefers_the_candidate_that_keeps_more_folders_of_the_old_path()
    {
        (Project project, List<MediaRef> missing) = ProjectWith(Media("a", @"D:\boda\día 2\toma.mp4"), Media("b", @"D:\boda\día 1\toma.mp4"));
        var disk = new FakeRelinkFileSystem()
            .Add("/n/día 1/toma.mp4", Probe(), head: "uno")
            .Add("/n/día 2/toma.mp4", Probe(), head: "dos");

        RelinkSearchResult result = MediaRelinkSearch.Search(project, missing, "/n", disk);

        Assert.Equal(
            [new RelinkMatch("a", "/n/día 2/toma.mp4", RelinkMatchKind.SameName), new RelinkMatch("b", "/n/día 1/toma.mp4", RelinkMatchKind.SameName)],
            result.Found);
        Assert.Equal(0, disk.Fingerprints);
    }

    [Fact]
    public void Identical_copies_are_resolved_with_the_fingerprint()
    {
        (Project project, List<MediaRef> missing) = ProjectWith(Media("a", @"C:\viejo\toma.mp4"));
        var disk = new FakeRelinkFileSystem()
            .Add("/n/copia 1/toma.mp4", Probe(), head: "igual")
            .Add("/n/copia 2/toma.mp4", Probe(), head: "igual");

        RelinkSearchResult result = MediaRelinkSearch.Search(project, missing, "/n", disk);

        Assert.Equal([new RelinkMatch("a", "/n/copia 1/toma.mp4", RelinkMatchKind.SameName)], result.Found);
        Assert.Equal(2, disk.Fingerprints);
    }

    [Fact]
    public void Different_files_that_fit_equally_are_left_to_choose_by_hand()
    {
        (Project project, List<MediaRef> missing) = ProjectWith(Media("a", @"C:\viejo\toma.mp4"));
        var disk = new FakeRelinkFileSystem()
            .Add("/n/x/toma.mp4", Probe(), head: "uno")
            .Add("/n/y/toma.mp4", Probe(), head: "dos");

        RelinkSearchResult result = MediaRelinkSearch.Search(project, missing, "/n", disk);

        Assert.Empty(result.Found);
        Assert.Equal(["/n/x/toma.mp4", "/n/y/toma.mp4"], result.Ambiguous["a"]);
        Assert.Empty(result.NotFound);
    }

    [Fact]
    public void A_file_is_never_given_to_two_media_nor_to_one_that_is_not_missing()
    {
        MediaRef present = Media("ok", "/n/presente.mp4");
        (Project project, _) = ProjectWith(present, Media("a", @"C:\v\uno.mp4"), Media("b", @"C:\v\dos.mp4"));
        List<MediaRef> missing = [project.Media[1], project.Media[2]];
        var disk = new FakeRelinkFileSystem()
            .Add("/n/presente.mp4", Probe())
            .Add("/n/uno.mp4", Probe());

        RelinkSearchResult result = MediaRelinkSearch.Search(project, missing, "/n", disk);

        // «uno.mp4» va por nombre a «a»; «b» no puede llevarse ni ese ni el del medio que sí está.
        Assert.Equal([new RelinkMatch("a", "/n/uno.mp4", RelinkMatchKind.SameName)], result.Found);
        Assert.Equal(["b"], result.NotFound);
    }

    [Fact]
    public void Stops_at_the_file_limit_and_says_so()
    {
        (Project project, List<MediaRef> missing) = ProjectWith(Media("a", "toma.mp4"));
        var disk = new FakeRelinkFileSystem().Add("/n/a.txt", null).Add("/n/b.txt", null).Add("/n/toma.mp4", Probe());

        RelinkSearchResult result = MediaRelinkSearch.Search(project, missing, "/n", disk, new RelinkSearchOptions { MaxFiles = 2 });

        Assert.True(result.Truncated);
        Assert.Equal(2, result.FilesScanned);
    }

    [Fact]
    public void Can_be_cancelled()
    {
        (Project project, List<MediaRef> missing) = ProjectWith(Media("a", "toma.mp4"));
        var disk = new FakeRelinkFileSystem().Add("/n/toma.mp4", Probe());
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => MediaRelinkSearch.Search(project, missing, "/n", disk, cancellationToken: cancel.Token));
    }

    [Fact]
    public void Works_with_real_folders_and_the_fingerprint_of_the_first_bytes()
    {
        string root = Directory.CreateTempSubdirectory("rodaje-reenlazar-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "a"));
            Directory.CreateDirectory(Path.Combine(root, "b"));
            Directory.CreateDirectory(Path.Combine(root, "c"));
            byte[] content = new byte[64 * 1024];
            new Random(7).NextBytes(content);
            File.WriteAllBytes(Path.Combine(root, "a", "toma.mp4"), content);
            File.WriteAllBytes(Path.Combine(root, "b", "toma.mp4"), content);
            File.WriteAllBytes(Path.Combine(root, "c", "voz.wav"), [1, 2, 3]);
            File.WriteAllBytes(Path.Combine(root, "c", "otra voz.wav"), [4, 5, 6, 7]);
            var disk = new DiskRelinkFileSystem(_ => Probe());
            (Project project, List<MediaRef> missing) = ProjectWith(Media("a", @"C:\viejo\toma.mp4"), Media("b", @"C:\viejo\voz.wav"));

            RelinkSearchResult result = MediaRelinkSearch.Search(project, missing, root, disk, new RelinkSearchOptions { FingerprintBytes = 1024 });

            Assert.Equal(Path.Combine(root, "a", "toma.mp4"), result.Found[0].Path);
            Assert.Equal(Path.Combine(root, "c", "voz.wav"), result.Found[1].Path);
            Assert.Equal(4, result.FilesScanned);

            string? one = disk.Fingerprint(Path.Combine(root, "c", "voz.wav"), 2);
            string? two = disk.Fingerprint(Path.Combine(root, "c", "otra voz.wav"), 2);
            Assert.NotNull(one);
            Assert.NotEqual(one, two);
            Assert.Null(disk.Fingerprint(Path.Combine(root, "no está.mp4"), 2));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(@"C:\a\b\toma.mp4", "toma.mp4")]
    [InlineData("/a/b/voz.wav", "voz.wav")]
    [InlineData("suelto.mov", "suelto.mov")]
    public void File_name_accepts_both_separators(string path, string expected) =>
        Assert.Equal(expected, MediaRelinkSearch.FileName(path));
}
