using Rodaje.App.Services;
using Rodaje.App.Tests.Support;

namespace Rodaje.App.Tests.Packages;

public sealed class InstanceRegistryTests : IDisposable
{
    private readonly TempDirectory _folder = new();

    public void Dispose() => _folder.Dispose();

    private string Folder => Path.Combine(_folder.Path, "instancias");

    [Fact]
    public void Each_open_instance_counts_and_closing_one_removes_its_file()
    {
        using InstanceRegistry first = InstanceRegistry.Register(Folder);
        Assert.True(first.IsRegistered);
        Assert.Equal(1, first.CountActive());

        InstanceRegistry second = InstanceRegistry.Register(Folder);
        Assert.Equal(2, first.CountActive());
        Assert.Equal(2, second.CountActive());

        second.Dispose();
        Assert.Equal(1, first.CountActive());
        Assert.Single(Directory.GetFiles(Folder));
    }

    [Fact]
    public void A_file_left_by_a_dead_process_does_not_count_and_is_removed()
    {
        Directory.CreateDirectory(Folder);
        string dead = Path.Combine(Folder, "999999-deadbeef" + InstanceRegistry.Extension);
        File.WriteAllText(dead, "");
        using InstanceRegistry alive = InstanceRegistry.Register(Folder);

        Assert.Equal(1, alive.CountActive());
        Assert.False(File.Exists(dead));
    }

    [Fact]
    public void An_instance_that_could_not_register_still_counts_itself_and_the_others()
    {
        using InstanceRegistry other = InstanceRegistry.Register(Folder);
        string blocked = Path.Combine(_folder.Path, "archivo");
        File.WriteAllText(blocked, "");
        using InstanceRegistry unregistered = InstanceRegistry.Register(blocked);

        Assert.False(unregistered.IsRegistered);
        Assert.Equal(1, unregistered.CountActive());
        Assert.Equal(1, InstanceRegistry.CountRegistered(Folder));
    }
}
