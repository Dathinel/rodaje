using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using CoreTime = Rodaje.Core.Timing.Time;

namespace Rodaje.Core.Tests.Editing.Relink;

/// <summary>La pasada de renombrados no abre archivos que solo están en la nube (tarea 48).</summary>
public class MediaRelinkCloudOnlyTests
{
    /// <summary>Un disco en memoria donde algunos archivos solo están en la nube.</summary>
    private sealed class CloudDisk : IRelinkFileSystem
    {
        private readonly Dictionary<string, bool> _files = new(StringComparer.Ordinal);

        public List<string> Probed { get; } = [];

        public CloudDisk Add(string path, bool cloudOnly)
        {
            _files[path] = cloudOnly;
            return this;
        }

        public IEnumerable<RelinkFile> EnumerateFiles(string folder, CancellationToken cancellationToken) =>
            _files.Where(f => f.Key.StartsWith(folder, StringComparison.Ordinal)).Select(f => new RelinkFile(f.Key, 1000, f.Value));

        public RelinkProbe? Probe(string path)
        {
            Probed.Add(path);
            return RelinkProbe.Of(Media("x", path));
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

    [Fact]
    public void Cloud_only_files_are_not_opened_when_looking_for_renamed_ones()
    {
        MediaRef a = Media("a", @"C:\viejo\a.mp4");
        var project = new Project { Id = "p", Name = "p" };
        project.Media.Add(a);
        var disk = new CloudDisk().Add("/nube/otro.mp4", cloudOnly: true).Add("/nube/tercero.mp4", cloudOnly: true);

        RelinkSearchResult result = MediaRelinkSearch.Search(project, [a], "/nube", disk);

        Assert.Equal(["a"], result.NotFound);
        Assert.Empty(disk.Probed);
    }

    [Fact]
    public void A_cloud_only_file_with_the_same_name_is_still_found()
    {
        MediaRef a = Media("a", @"C:\viejo\a.mp4");
        var project = new Project { Id = "p", Name = "p" };
        project.Media.Add(a);
        var disk = new CloudDisk().Add("/nube/a.mp4", cloudOnly: true).Add("/nube/otro.mp4", cloudOnly: true);

        RelinkSearchResult result = MediaRelinkSearch.Search(project, [a], "/nube", disk);

        Assert.Equal([new RelinkMatch("a", "/nube/a.mp4", RelinkMatchKind.SameName)], result.Found);
        Assert.Equal(["/nube/a.mp4"], disk.Probed);
    }

    [Fact]
    public void The_disk_marks_offline_and_recall_on_data_access_as_cloud_only()
    {
        Assert.NotEqual(default, DiskRelinkFileSystem.CloudOnlyAttributes & FileAttributes.Offline);
        Assert.NotEqual(default, DiskRelinkFileSystem.CloudOnlyAttributes & (FileAttributes)0x400000);
    }
}
