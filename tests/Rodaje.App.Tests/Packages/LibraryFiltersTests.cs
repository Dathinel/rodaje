using Rodaje.App.Services.Effects;
using Rodaje.App.Services.Packages;
using Rodaje.App.Tests.Support;
using Rodaje.Core.Model;
using Rodaje.Packages;
using Rodaje.Packages.Content;

namespace Rodaje.App.Tests.Packages;

public sealed class LibraryFiltersTests : IDisposable
{
    private readonly TempDirectory _folder = new();

    public void Dispose() => _folder.Dispose();

    [Fact]
    public void Una_LUT_que_usa_un_filtro_del_paquete_sale_solo_como_filtro()
    {
        PackageScan scan = Scan();

        IReadOnlyList<LibraryElement> filters = LibraryElements.For(LibraryCategory.Filters, scan);

        Assert.Equal(["Color y LUT", "Cálido", "LUT suelta"], filters.Select(f => f.Name));
        Assert.Equal("looks@1.0.0/lut-suelta", filters[2].Reference);
        Assert.Equal(ItemKind.Lut, filters[2].Kind);
    }

    [Fact]
    public void El_filtro_se_lee_con_su_LUT_como_referencia_completa()
    {
        var loader = new InstalledContentLoader(Scan);

        FilterPreset filter = loader.LoadFilter("looks@1.0.0/calido")!;

        Assert.Equal("looks@1.0.0/lut-calido", filter.Effects[0].Options["lut"]);
        Assert.Equal(2, loader.LoadLut(filter.Effects[0].Options["lut"])!.Size);
    }

    private PackageScan Scan()
    {
        var locations = PackageLocations.Under(_folder.Path);
        string package = Path.Combine(locations.Packages, "looks");
        Directory.CreateDirectory(Path.Combine(package, "luts"));
        File.WriteAllText(Path.Combine(package, PackageManifest.FileName), """
            {
              "formatVersion": 1, "id": "looks", "version": "1.0.0", "kind": "content", "name": "Looks", "author": "Dathinel",
              "license": "CC0-1.0", "apps": ["rodaje"],
              "items": [
                { "id": "calido", "kind": "filter", "name": "Cálido", "file": "calido.json" },
                { "id": "lut-calido", "kind": "lut", "name": "LUT Cálido", "file": "luts/calido.cube" },
                { "id": "lut-suelta", "kind": "lut", "name": "LUT suelta", "file": "luts/calido.cube" }
              ]
            }
            """);
        File.WriteAllText(Path.Combine(package, "calido.json"), """
            {
              "format": "rodaje.filter", "version": 1, "id": "calido", "name": "Cálido",
              "effects": [{ "type": "rodaje.color", "parameters": { "lutIntensity": 1 }, "options": { "lut": "./lut-calido" } }]
            }
            """);
        File.WriteAllText(Path.Combine(package, "luts", "calido.cube"), """
            LUT_3D_SIZE 2
            0 0 0
            1 0 0
            0 1 0
            1 1 0
            0 0 1
            1 0 1
            0 1 1
            1 1 1
            """);
        return PackageLibrary.Scan(locations, "rodaje");
    }
}
