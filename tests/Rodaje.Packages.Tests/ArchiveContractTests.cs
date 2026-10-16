using System.IO.Compression;
using System.Text;

namespace Rodaje.Packages.Tests;

/// <summary>Contrato de la sección «El archivo» de <c>docs/formato-paquete.md</c>: cada prueba lleva encima la frase que comprueba.</summary>
public sealed class ArchiveContractTests : IDisposable
{
    private const string ExampleItems = """
        [
          { "id": "deslizar", "kind": "transition", "name": "Deslizar", "file": "transiciones/deslizar.json", "preview": "vistas/deslizar.png" },
          { "id": "zoom-suave", "kind": "transition", "name": "Zoom suave", "file": "transiciones/zoom-suave.json" }
        ]
        """;

    private static readonly string[] ExampleFiles = ["paquete.json", "transiciones/deslizar.json", "transiciones/zoom-suave.json", "vistas/deslizar.png"];

    private readonly TempFolder _folder = new();
    private readonly PackageLocations _locations;
    private readonly PackageInstaller _installer;

    public ArchiveContractTests()
    {
        _locations = PackageLocations.Under(_folder.Combine("complementos"));
        _installer = new PackageInstaller(_locations, "rodaje");
    }

    public void Dispose() => _folder.Dispose();

    // «Un `.paquete` es un zip normal.»
    [Theory]
    [InlineData(CompressionLevel.Optimal)]
    [InlineData(CompressionLevel.NoCompression)]
    public void A_plain_zip_made_from_a_folder_installs(CompressionLevel level)
    {
        string source = WriteExampleFolder(_folder.Combine("fuente"));
        string archive = _folder.Combine("rodaje-transiciones.paquete");
        ZipFile.CreateFromDirectory(source, archive, level, includeBaseDirectory: false);

        InstallResult result = _installer.Install(archive);

        Assert.Equal(Path.Combine(_locations.Packages, "rodaje-transiciones"), result.Package.Directory);
        Assert.Equal(ExampleFiles, RelativeFiles(result.Package.Directory));
    }

    // «En la raíz va `paquete.json`; el resto son los recursos que nombran sus elementos.»
    [Fact]
    public void The_example_tree_installs_with_its_manifest_at_the_root_and_its_resources()
    {
        string archive = Samples.Zip(_folder.Combine("t.paquete"),
            ("paquete.json", Samples.Manifest(items: ExampleItems)),
            ("transiciones/deslizar.json", "{}"),
            ("transiciones/zoom-suave.json", "{}"),
            ("vistas/deslizar.png", "png"));

        InstallResult result = _installer.Install(archive);

        Assert.Equal(["deslizar", "zoom-suave"], result.Package.Manifest.Items.Select(i => i.Id));
        Assert.Equal(ExampleFiles, RelativeFiles(result.Package.Directory));
    }

    // «En la raíz va `paquete.json`»
    [Fact]
    public void Without_a_manifest_at_the_root_nothing_is_installed()
    {
        string archive = Samples.Zip(_folder.Combine("t.paquete"),
            ("transiciones/deslizar.json", "{}"),
            ("otra/paquete.json", Samples.Manifest()));

        Assert.Throws<PackageInstallException>(() => _installer.Install(archive));
        Assert.Empty(Directory.GetFileSystemEntries(_locations.Packages));
    }

    // «el resto son los recursos que nombran sus elementos»
    [Fact]
    public void A_resource_named_by_an_item_but_missing_from_the_zip_is_refused()
    {
        string archive = Samples.Zip(_folder.Combine("t.paquete"),
            ("paquete.json", Samples.Manifest(items: ExampleItems)),
            ("transiciones/deslizar.json", "{}"),
            ("transiciones/zoom-suave.json", "{}"));

        PackageInstallException error = Assert.Throws<PackageInstallException>(() => _installer.Install(archive));
        Assert.Contains("vistas/deslizar.png", error.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFileSystemEntries(_locations.Packages));
    }

    // «Las releases de GitHub suelen envolver todo en una carpeta (`rodaje-transiciones-1.0.0/paquete.json`); el instalador lo
    // reconoce y quita esa carpeta.»
    [Fact]
    public void A_release_style_zip_with_folder_entries_loses_its_wrapping_folder()
    {
        string source = _folder.Combine("fuente");
        WriteExampleFolder(Path.Combine(source, "rodaje-transiciones-1.0.0"));
        string archive = _folder.Combine("rodaje-transiciones-1.0.0.zip");
        ZipFile.CreateFromDirectory(Path.Combine(source, "rodaje-transiciones-1.0.0"), archive, CompressionLevel.Optimal, includeBaseDirectory: true);
        using (ZipArchive check = ZipFile.OpenRead(archive))
        {
            Assert.All(check.Entries, e => Assert.StartsWith("rodaje-transiciones-1.0.0/", e.FullName, StringComparison.Ordinal));
        }

        InstallResult result = _installer.Install(archive);

        Assert.Equal(Path.Combine(_locations.Packages, "rodaje-transiciones"), result.Package.Directory);
        Assert.Equal(ExampleFiles, RelativeFiles(result.Package.Directory));
    }

    // «el instalador lo reconoce y quita esa carpeta» (con las carpetas declaradas como entradas, como en las releases)
    [Fact]
    public void A_wrapping_folder_with_explicit_directory_entries_is_removed()
    {
        string archive = Samples.Zip(_folder.Combine("t.paquete"),
            ("rodaje-transiciones-1.0.0/", ""),
            ("rodaje-transiciones-1.0.0/paquete.json", Samples.Manifest(items: ExampleItems)),
            ("rodaje-transiciones-1.0.0/transiciones/", ""),
            ("rodaje-transiciones-1.0.0/transiciones/deslizar.json", "{}"),
            ("rodaje-transiciones-1.0.0/transiciones/zoom-suave.json", "{}"),
            ("rodaje-transiciones-1.0.0/vistas/", ""),
            ("rodaje-transiciones-1.0.0/vistas/deslizar.png", "png"));

        InstallResult result = _installer.Install(archive);

        Assert.Equal(ExampleFiles, RelativeFiles(result.Package.Directory));
        Assert.False(Directory.Exists(Path.Combine(result.Package.Directory, "rodaje-transiciones-1.0.0")));
    }

    // «envolver todo en una carpeta»: si no está todo dentro de esa carpeta, no hay envoltorio que quitar.
    [Fact]
    public void A_folder_that_does_not_wrap_everything_is_not_removed()
    {
        string archive = Samples.Zip(_folder.Combine("t.paquete"),
            ("rodaje-transiciones-1.0.0/paquete.json", Samples.Manifest()),
            ("rodaje-transiciones-1.0.0/transiciones/deslizar.json", "{}"),
            ("LEEME.txt", "suelto"));

        Assert.Throws<PackageInstallException>(() => _installer.Install(archive));
        Assert.Empty(Directory.GetFileSystemEntries(_locations.Packages));
    }

    private static string WriteExampleFolder(string directory)
    {
        Directory.CreateDirectory(Path.Combine(directory, "transiciones"));
        Directory.CreateDirectory(Path.Combine(directory, "vistas"));
        File.WriteAllText(Path.Combine(directory, "paquete.json"), Samples.Manifest(items: ExampleItems), Encoding.UTF8);
        File.WriteAllText(Path.Combine(directory, "transiciones", "deslizar.json"), "{}");
        File.WriteAllText(Path.Combine(directory, "transiciones", "zoom-suave.json"), "{}");
        File.WriteAllBytes(Path.Combine(directory, "vistas", "deslizar.png"), [0x89, 0x50, 0x4E, 0x47]);
        return directory;
    }

    private static string[] RelativeFiles(string directory) =>
        [.. Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(directory, f).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)];
}
