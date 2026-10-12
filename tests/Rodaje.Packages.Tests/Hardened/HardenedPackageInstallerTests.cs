namespace Rodaje.Packages.Tests.Hardened;

/// <summary>
/// Versión endurecida de <c>PackageInstallerTests.A_wrapping_folder_like_github_release_zips_is_removed</c>: allí solo
/// se miraba que existiera un archivo dentro de la carpeta que devolvía el instalador, sin comprobar cuál era esa carpeta.
/// Un instalador que dejara la carpeta envoltorio («…/rodaje-transiciones/rodaje-transiciones-1.0.0/…») y devolviera esa
/// ruta, o que dejara restos de la extracción, seguía en verde.
/// </summary>
public sealed class HardenedPackageInstallerTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly PackageLocations _locations;
    private readonly PackageInstaller _installer;

    public HardenedPackageInstallerTests()
    {
        _locations = PackageLocations.Under(_folder.Combine("complementos"));
        _installer = new PackageInstaller(_locations, "rodaje");
    }

    public void Dispose() => _folder.Dispose();

    [Fact]
    public void A_wrapping_folder_is_removed_and_the_package_lands_in_its_own_folder_with_nothing_else()
    {
        string archive = Samples.Zip(_folder.Combine("t.paquete"),
            ("rodaje-transiciones-1.0.0/paquete.json", Samples.Manifest()),
            ("rodaje-transiciones-1.0.0/transiciones/deslizar.json", "{\"envuelto\":true}"));

        InstallResult result = _installer.Install(archive);

        Assert.Equal(InstallOutcome.Installed, result.Outcome);
        string target = Path.Combine(_locations.Packages, "rodaje-transiciones");
        Assert.Equal(target, result.Package.Directory);

        // Dentro quedan exactamente los archivos del paquete, sin la carpeta envoltorio.
        string[] files = [.. Directory.GetFiles(target, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(target, f).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)];
        Assert.Equal(["paquete.json", "transiciones/deslizar.json"], files);
        Assert.Equal("{\"envuelto\":true}", File.ReadAllText(Path.Combine(target, "transiciones", "deslizar.json")));
        Assert.Equal(Samples.Manifest(), File.ReadAllText(Path.Combine(target, "paquete.json")));

        // En la carpeta de complementos no queda nada más (ni la carpeta temporal de la extracción).
        Assert.Equal(["rodaje-transiciones"], Directory.GetDirectories(_locations.Packages).Select(Path.GetFileName));
        Assert.Empty(Directory.GetFiles(_locations.Packages));
    }
}
