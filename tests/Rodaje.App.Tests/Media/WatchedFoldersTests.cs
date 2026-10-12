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

    private static string Write(string folder, string name, int bytes)
    {
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, name);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }
}
