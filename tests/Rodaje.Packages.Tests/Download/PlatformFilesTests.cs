using Rodaje.Packages.Download;

namespace Rodaje.Packages.Tests.Download;

/// <summary>Lo de Packages que depende del sistema (D-066): el índice de cada uno, enlaces duros y el bit de ejecución.</summary>
public sealed class PlatformFilesTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("rodaje-plataforma-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData("win-x64", "indice.json")]
    [InlineData("win-arm64", "indice.json")]
    [InlineData("linux-x64", "indice-linux-x64.json")]
    [InlineData("osx-arm64", "indice-osx-arm64.json")]
    public void Each_system_reads_its_own_index_and_windows_keeps_the_old_name(string rid, string expected) =>
        Assert.Equal(expected, CatalogIndex.FileName(rid));

    [Theory]
    [InlineData("../x")]
    [InlineData("Linux-X64")]
    [InlineData(" ")]
    public void A_strange_rid_is_refused(string rid) =>
        Assert.ThrowsAny<ArgumentException>(() => CatalogIndex.FileName(rid));

    [Fact]
    public void This_system_reads_the_expected_index()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("indice.json", CatalogIndex.ForThisSystem);
        }
        else
        {
            Assert.StartsWith("indice-", CatalogIndex.ForThisSystem, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_model_is_hard_linked_on_the_same_volume()
    {
        string source = Path.Combine(_root, "modelo");
        string target = Path.Combine(_root, "modulo", "modelo.onnx");
        File.WriteAllText(source, "uno");

        ModelStore.LinkOrCopy(source, target);
        File.AppendAllText(target, "dos");

        // Con enlace duro los dos nombres son el mismo archivo; una copia no vería el cambio.
        Assert.Equal("unodos", File.ReadAllText(source));
    }

    [Fact]
    public void The_module_entry_becomes_executable_and_nothing_else()
    {
        string entry = Path.Combine(_root, "bin", "modulo");
        string other = Path.Combine(_root, "datos.json");
        Directory.CreateDirectory(Path.GetDirectoryName(entry)!);
        File.WriteAllText(entry, "#!/bin/sh\n");
        File.WriteAllText(other, "{}");

        PackageInstaller.MakeExecutable(_root, "bin/modulo");
        PackageInstaller.MakeExecutable(_root, "../fuera");

        if (!OperatingSystem.IsWindows())
        {
            Assert.True(File.GetUnixFileMode(entry).HasFlag(UnixFileMode.UserExecute));
            Assert.False(File.GetUnixFileMode(other).HasFlag(UnixFileMode.UserExecute));
        }
    }
}
