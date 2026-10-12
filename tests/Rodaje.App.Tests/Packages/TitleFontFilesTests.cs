using Rodaje.App.Services.Titles;
using Rodaje.App.Tests.Support;
using Rodaje.Packages;

namespace Rodaje.App.Tests.Packages;

/// <summary>Solo se registran los archivos de los elementos <c>font</c> (D-029), <c>rodaje-fuentes</c> primero.</summary>
public sealed class TitleFontFilesTests : IDisposable
{
    private readonly TempDirectory _folder = new();

    public void Dispose() => _folder.Dispose();

    [Fact]
    public void SoloLasFuentesDeclaradasDentroDelPaqueteYLasDeRodajeFuentesPrimero()
    {
        InstalledPackage other = Package("otros", "a-otros",
            new PackageItem("serif", ItemKind.Font, "Serif", License: "OFL-1.1", Files: ["f/Serif.ttf", "f/NoEsta.ttf", "../fuera.ttf"]),
            new PackageItem("clasico", ItemKind.TextStyle, "Clásico", "estilos/clasico.json"));
        File.WriteAllText(Path.Combine(other.Directory, "suelta.ttf"), "");
        File.WriteAllText(_folder.File("fuera.ttf"), "");
        InstalledPackage fonts = Package(TitleFontFiles.FontsPackage, "z-fuentes",
            new PackageItem("inter", ItemKind.Font, "Inter", License: "OFL-1.1", Files: ["Inter-Bold.otf"]));

        IReadOnlyList<string> found = TitleFontFiles.Find(new PackageScan([other, fonts], []));

        Assert.Equal([Path.Combine(fonts.Directory, "Inter-Bold.otf"), Path.Combine(other.Directory, "f", "Serif.ttf")], found);
    }

    private InstalledPackage Package(string id, string folder, params PackageItem[] items)
    {
        string directory = _folder.File(folder);
        foreach (string file in items.SelectMany(i => i.Files ?? []).Where(f => !f.Contains("NoEsta", StringComparison.Ordinal) && !f.StartsWith("..", StringComparison.Ordinal)))
        {
            string full = Path.Combine(directory, file);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, "");
        }

        Directory.CreateDirectory(directory);
        var manifest = new PackageManifest
        {
            FormatVersion = 1,
            Id = id,
            Version = PackageVersion.Parse("1.0.0"),
            Kind = PackageKind.Content,
            Name = id,
            Author = "Dathinel",
            License = "MIT",
            Apps = ["rodaje"],
            Items = items,
        };
        return new InstalledPackage(manifest, directory, PackageOrigin.Installed);
    }
}
