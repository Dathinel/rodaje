using Rodaje.App.Services.Media;
using Rodaje.App.Services.Settings;
using Rodaje.App.Tests.Support;

namespace Rodaje.App.Tests.Media;

public class WatchedFoldersTests
{
    [Fact]
    public void SoloLoNuevoYaCopiadoYConExtensionDeMedio()
    {
        using var dir = new TempDirectory();
        string old = Write(dir.Path, "viejo.mp4", 10);
        File.SetCreationTimeUtc(old, DateTime.UtcNow.AddDays(-2));
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-2));
        var settings = new AppSettings();
        Assert.True(WatchedFolderScanner.Add(settings, dir.Path, DateTime.UtcNow.AddMinutes(-1)));
        Assert.False(WatchedFolderScanner.Add(settings, dir.Path + "\\", DateTime.UtcNow));

        string clip = Write(Path.Combine(dir.Path, "DCIM", "100CANON"), "MVI_0001.MOV", 100);
        Write(dir.Path, "notas.txt", 5);
        string inLibrary = Write(dir.Path, "ya.mp4", 10);
        string copying = Write(dir.Path, "copiando.mp4", 10);
        var scanner = new WatchedFolderScanner();
        bool InLibrary(string p) => string.Equals(p, inLibrary, StringComparison.OrdinalIgnoreCase);

        // La primera pasada solo anota tamaños: un archivo cuenta cuando no creció entre dos pasadas.
        Assert.Empty(scanner.Scan(settings.WatchedFolders, settings.HandledWatchedFiles, InLibrary));
        File.AppendAllText(copying, "más");
        IReadOnlyList<WatchedFile> found = scanner.Scan(settings.WatchedFolders, settings.HandledWatchedFiles, InLibrary);
        Assert.Equal([clip], found.Select(f => f.Path));
        Assert.Equal(Path.GetFileName(dir.Path), found[0].FolderName);

        IReadOnlyList<WatchedFile> next = scanner.Scan(settings.WatchedFolders, settings.HandledWatchedFiles, InLibrary);
        Assert.Equal(2, next.Count);
        WatchedFolderScanner.MarkHandled(settings, next.Select(f => f.Path));
        Assert.Empty(scanner.Scan(settings.WatchedFolders, settings.HandledWatchedFiles, InLibrary));
    }

    [Fact]
    public void LaBandejaAvisaUnaVezYDescartarLoAnota()
    {
        var settings = new AppSettings();
        var session = new Rodaje.App.Services.EditorSession(new FakePlayback(), new FakeProber(), new FakeDialogs());
        var inbox = new WatchedFolderInbox(session, () => settings);
        int arrived = 0;
        int changed = 0;
        inbox.Arrived += (_, files) => arrived += files.Count;
        inbox.Changed += (_, _) => changed++;
        WatchedFile[] files = [new(@"C:\camara\a.mp4", "camara", 1), new(@"C:\camara\b.mp4", "camara", 1)];

        inbox.Update(files);
        inbox.Update(files);
        Assert.Equal(2, arrived);
        Assert.Equal(1, changed);
        Assert.Equal("2 medios nuevos en «camara»", WatchedFolderInbox.Describe(inbox.Pending));

        inbox.Dismiss();
        Assert.Empty(inbox.Pending);
        Assert.Equal(2, settings.HandledWatchedFiles.Count);
        inbox.Update(files);
        Assert.Empty(inbox.Pending);
    }

    [Fact]
    public void LaBandeja_SiLaImportacionFalla_LosVuelveAOfrecer() => UiThread.Run(async () =>
    {
        var settings = new AppSettings();
        var prober = new SwitchableProber();
        var session = new Rodaje.App.Services.EditorSession(new FakePlayback(), prober, new FakeDialogs());
        var inbox = new WatchedFolderInbox(session, () => settings);
        WatchedFile[] files = [new(Path.GetFullPath("camara-a.mp4"), "camara", 1)];
        inbox.Update(files);

        await Assert.ThrowsAsync<IOException>(inbox.ImportAsync);
        Assert.Empty(settings.HandledWatchedFiles);
        inbox.Update(files);
        Assert.Single(inbox.Pending);

        prober.Down = false;
        await inbox.ImportAsync();
        Assert.Single(session.Project.Media);
        Assert.Single(settings.HandledWatchedFiles);
        inbox.Update(files);
        Assert.Empty(inbox.Pending);
    });

    [Fact]
    public void SoltarUnaCarpeta_EntraConSusMedios()
    {
        using var dir = new TempDirectory();
        string clip = Write(Path.Combine(dir.Path, "DCIM", "100CANON"), "MVI_0002.MXF", 10);
        string photo = Write(dir.Path, "a.jpg", 10);
        Write(dir.Path, "notas.txt", 5);
        string loose = dir.File("suelto.rodaje");

        Assert.Equal([photo, clip, loose], WatchedFolderScanner.ExpandFolders([dir.Path, loose]));
    }

    /// <summary>Un disco que se desconecta: mientras <see cref="Down"/>, leer falla; si no, todo es un video.</summary>
    private sealed class SwitchableProber : Rodaje.App.Services.IMediaProber
    {
        private readonly FakeProber _video = new FakeProber().Video("camara-a.mp4");

        public bool Down { get; set; } = true;

        public Rodaje.App.Services.MediaProbeResult Probe(string path) => Down ? throw new IOException("El disco se desconectó.") : _video.Probe(path);
    }

    private static string Write(string folder, string name, int bytes)
    {
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, name);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }
}
