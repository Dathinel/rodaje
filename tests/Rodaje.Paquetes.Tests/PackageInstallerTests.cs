using System.IO.Compression;

namespace Rodaje.Paquetes.Tests;

public sealed class PackageInstallerTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly PackageLocations _locations;
    private readonly PackageInstaller _installer;

    public PackageInstallerTests()
    {
        _locations = PackageLocations.Under(_folder.Combine("complementos"));
        _installer = new PackageInstaller(_locations, "rodaje");
    }

    public void Dispose() => _folder.Dispose();

    [Fact]
    public void Installs_a_package_into_its_own_folder()
    {
        string archive = Samples.ValidPackage(_folder.Combine("t.paquete"));

        InstallResult result = _installer.Install(archive);

        Assert.Equal(InstallOutcome.Installed, result.Outcome);
        string target = Path.Combine(_locations.Packages, "rodaje-transiciones");
        Assert.Equal(target, result.Package.Directory);
        Assert.True(File.Exists(Path.Combine(target, "paquete.json")));
        Assert.True(File.Exists(Path.Combine(target, "transiciones", "deslizar.json")));
        Assert.Equal(["rodaje-transiciones"], Directory.GetDirectories(_locations.Packages).Select(Path.GetFileName));
    }

    [Fact]
    public void A_wrapping_folder_like_github_release_zips_is_removed()
    {
        string archive = Samples.Zip(_folder.Combine("t.paquete"),
            ("rodaje-transiciones-1.0.0/paquete.json", Samples.Manifest()),
            ("rodaje-transiciones-1.0.0/transiciones/deslizar.json", "{}"));

        InstallResult result = _installer.Install(archive);

        Assert.True(File.Exists(Path.Combine(result.Package.Directory, "transiciones", "deslizar.json")));
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("..\\evil.txt")]
    [InlineData("transiciones/../../evil.txt")]
    [InlineData("/evil.txt")]
    [InlineData("C:/evil.txt")]
    [InlineData("evil.txt:flujo")]
    [InlineData("con.txt")]
    public void Path_traversal_entries_abort_and_write_nothing_outside(string entry)
    {
        string archive = Samples.Zip(_folder.Combine("malo.paquete"),
            ("paquete.json", Samples.Manifest()), ("transiciones/deslizar.json", "{}"), (entry, "pwned"));

        var ex = Assert.Throws<PackageInstallException>(() => _installer.Install(archive));

        Assert.Contains("ruta no permitida", ex.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(_folder.Combine("evil.txt")));
        Assert.False(File.Exists(_folder.Combine("complementos", "evil.txt")));
        Assert.False(File.Exists(Path.Combine(_locations.Packages, "evil.txt")));
        Assert.Empty(Directory.GetDirectories(_locations.Packages));
    }

    [Fact]
    public void Repeated_entries_that_differ_only_in_case_are_refused()
    {
        string archive = Samples.Zip(_folder.Combine("dup.paquete"),
            ("paquete.json", Samples.Manifest()), ("transiciones/deslizar.json", "{}"), ("Transiciones/Deslizar.json", "{\"otro\":1}"));

        var ex = Assert.Throws<PackageInstallException>(() => _installer.Install(archive));
        Assert.Contains("repite", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Suspicious_compression_ratios_are_refused()
    {
        string path = _folder.Combine("bomba.paquete");
        using (var stream = new FileStream(path, FileMode.Create))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("paquete.json").Open()))
            {
                writer.Write(Samples.Manifest());
            }

            using Stream big = zip.CreateEntry("transiciones/deslizar.json", CompressionLevel.SmallestSize).Open();
            big.Write(new byte[16 << 20]);
        }

        var ex = Assert.Throws<PackageInstallException>(() => new PackageInstaller(_locations, "rodaje", new InstallLimits { MaxCompressionRatio = 100 }).Install(path));
        Assert.Contains("sospechosa", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Too_many_entries_or_too_many_bytes_are_refused()
    {
        string archive = Samples.ValidPackage(_folder.Combine("t.paquete"));

        Assert.Throws<PackageInstallException>(() => new PackageInstaller(_locations, "rodaje", new InstallLimits { MaxEntries = 1 }).Install(archive));
        Assert.Throws<PackageInstallException>(() => new PackageInstaller(_locations, "rodaje", new InstallLimits { MaxTotalBytes = 10 }).Install(archive));
    }

    [Fact]
    public void An_invalid_manifest_leaves_nothing_behind()
    {
        string archive = Samples.Zip(_folder.Combine("roto.paquete"), ("paquete.json", "{ \"formatVersion\": 1 }"));

        var ex = Assert.Throws<PackageInstallException>(() => _installer.Install(archive));

        Assert.Contains("no es válido", ex.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.GetDirectories(_locations.Packages));
    }

    [Fact]
    public void A_package_for_another_program_is_refused()
    {
        string archive = Samples.ValidPackage(_folder.Combine("otro.paquete"), apps: "[\"editor-audio\"]");

        var ex = Assert.Throws<PackageInstallException>(() => _installer.Install(archive));
        Assert.Contains("editor-audio", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Updates_reinstalls_and_refuses_downgrades_unless_asked()
    {
        InstallResult first = _installer.Install(Samples.ValidPackage(_folder.Combine("v1.paquete"), version: "1.0.0"));
        InstallResult update = _installer.Install(Samples.ValidPackage(_folder.Combine("v2.paquete"), version: "1.1.0"));
        InstallResult same = _installer.Install(Samples.ValidPackage(_folder.Combine("v2b.paquete"), version: "1.1.0"));
        string older = Samples.ValidPackage(_folder.Combine("v0.paquete"), version: "0.9.0");
        var refused = Assert.Throws<PackageInstallException>(() => _installer.Install(older));
        InstallResult downgraded = _installer.Install(older, allowDowngrade: true);

        Assert.Equal(InstallOutcome.Installed, first.Outcome);
        Assert.Equal(InstallOutcome.Updated, update.Outcome);
        Assert.Equal(PackageVersion.Parse("1.0.0"), update.PreviousVersion);
        Assert.Equal(InstallOutcome.Reinstalled, same.Outcome);
        Assert.Contains("más nueva", refused.Message, StringComparison.Ordinal);
        Assert.Equal(InstallOutcome.Downgraded, downgraded.Outcome);
        Assert.Equal(["rodaje-transiciones"], Directory.GetDirectories(_locations.Packages).Select(Path.GetFileName));
        Assert.Equal("0.9.0", PackageLibrary.Scan(_locations, "rodaje").Find("rodaje-transiciones")!.Manifest.Version.ToString());
    }

    [Fact]
    public void Hash_is_checked_when_given()
    {
        string archive = Samples.ValidPackage(_folder.Combine("t.paquete"));
        string hash = PackageInstaller.ComputeSha256(archive);

        var ex = Assert.Throws<PackageInstallException>(() => _installer.Install(archive, expectedSha256: new string('0', 64)));
        Assert.Contains(hash, ex.Message, StringComparison.Ordinal);
        Assert.Equal(InstallOutcome.Installed, _installer.Install(archive, expectedSha256: hash.ToUpperInvariant()).Outcome);
    }

    [Fact]
    public void Modules_go_to_the_modules_folder()
    {
        string manifest = Samples.Manifest(id: "modulo-silencios", kind: "module", items: "[]",
            extra: ",\n\"module\": { \"entry\": \"bin/silencios.exe\", \"protocol\": 1, \"functions\": [\"silencios\"] }");
        string archive = Samples.Zip(_folder.Combine("m.paquete"), ("paquete.json", manifest), ("bin/silencios.exe", "MZ"));

        InstallResult result = _installer.Install(archive);

        Assert.Equal(Path.Combine(_locations.Modules, "modulo-silencios"), result.Package.Directory);
        Assert.Empty(Directory.GetDirectories(_locations.Packages));
    }

    [Fact]
    public void Uninstall_removes_the_folder_and_refuses_unknown_or_own()
    {
        _installer.Install(Samples.ValidPackage(_folder.Combine("t.paquete")));

        _installer.Uninstall("rodaje-transiciones");

        Assert.Empty(Directory.GetDirectories(_locations.Packages));
        Assert.Throws<PackageInstallException>(() => _installer.Uninstall("rodaje-transiciones"));
        Assert.Throws<PackageInstallException>(() => _installer.Uninstall(PackageLibrary.OwnPackageId));
        Assert.Throws<PackageInstallException>(() => _installer.Uninstall("../complementos"));
    }

    [Fact]
    public void Leftovers_of_an_interrupted_install_are_cleaned()
    {
        Directory.CreateDirectory(Path.Combine(_locations.Packages, ".instalando-abc", "x"));
        Directory.CreateDirectory(Path.Combine(_locations.Modules, ".desinstalando-def"));
        Directory.CreateDirectory(Path.Combine(_locations.Packages, "rodaje-titulos"));

        Assert.Equal(2, _installer.CleanUpLeftovers());
        Assert.Equal(["rodaje-titulos"], Directory.GetDirectories(_locations.Packages).Select(Path.GetFileName));
    }

    [Fact]
    public void A_blackout_between_the_two_renames_of_an_update_restores_the_previous_version()
    {
        string target = InstallVersion("1.0.0");
        Directory.Move(target, Path.Combine(_locations.Packages, ".anterior-0123456789ab-rodaje-transiciones"));

        Assert.Equal(1, _installer.CleanUpLeftovers());

        Assert.Equal("1.0.0", InstalledVersion());
        Assert.Equal(["rodaje-transiciones"], Directory.GetDirectories(_locations.Packages).Select(Path.GetFileName));
    }

    [Fact]
    public void A_blackout_that_leaves_the_new_version_incomplete_restores_the_previous_one()
    {
        string target = InstallVersion("1.0.0");
        Directory.Move(target, Path.Combine(_locations.Packages, ".anterior-0123456789ab-rodaje-transiciones"));
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "paquete.json"), Samples.Manifest(version: "2.0.0"));

        _installer.CleanUpLeftovers();

        Assert.Equal("1.0.0", InstalledVersion());
        Assert.True(File.Exists(Path.Combine(target, "transiciones", "deslizar.json")));
        Assert.Equal(["rodaje-transiciones"], Directory.GetDirectories(_locations.Packages).Select(Path.GetFileName));
    }

    [Fact]
    public void A_blackout_while_deleting_the_previous_version_keeps_the_new_one()
    {
        InstallVersion("1.0.0");
        string aside = Path.Combine(_locations.Packages, ".anterior-0123456789ab-rodaje-transiciones");
        Directory.CreateDirectory(Path.Combine(aside, "transiciones"));
        File.WriteAllText(Path.Combine(aside, "transiciones", "deslizar.json"), "{}");
        InstallVersion("2.0.0");

        _installer.CleanUpLeftovers();

        Assert.Equal("2.0.0", InstalledVersion());
        Assert.False(Directory.Exists(aside));
    }

    [Fact]
    public void A_blackout_during_an_uninstall_does_not_bring_the_package_back()
    {
        string target = InstallVersion("1.0.0");
        Directory.Move(target, Path.Combine(_locations.Packages, ".desinstalando-0123456789ab"));

        _installer.CleanUpLeftovers();

        Assert.Empty(Directory.GetDirectories(_locations.Packages));
    }

    [Fact]
    public void An_update_leaves_no_previous_version_behind()
    {
        InstallVersion("1.0.0");
        InstallVersion("1.1.0");

        Assert.Equal(["rodaje-transiciones"], Directory.GetDirectories(_locations.Packages).Select(Path.GetFileName));
        Assert.Equal(0, _installer.CleanUpLeftovers());
    }

    private string InstallVersion(string version)
    {
        string archive = Samples.ValidPackage(_folder.Combine($"v{version}.paquete"), version: version);
        return _installer.Install(archive).Package.Directory;
    }

    private string? InstalledVersion() =>
        ManifestReader.ReadFile(Path.Combine(_locations.Packages, "rodaje-transiciones", "paquete.json")).Manifest?.Version.ToString();

    [Fact]
    public void The_own_id_is_reserved()
    {
        string archive = Samples.ValidPackage(_folder.Combine("p.paquete"), id: PackageLibrary.OwnPackageId);

        Assert.Throws<PackageInstallException>(() => _installer.Install(archive));
    }
}
