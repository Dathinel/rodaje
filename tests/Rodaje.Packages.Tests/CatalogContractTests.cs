namespace Rodaje.Packages.Tests;

/// <summary>Promesas de la sección «Catálogo» de <c>docs/formato-paquete.md</c>, una prueba por frase.</summary>
public sealed class CatalogContractTests : IDisposable
{
    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private static PackageCatalog Bundled(List<string>? problems = null)
    {
        string? folder = AppContext.BaseDirectory;
        while (folder is not null && !File.Exists(Path.Combine(folder, "Directory.Build.props")))
        {
            folder = Path.GetDirectoryName(folder);
        }

        Assert.NotNull(folder);
        return PackageCatalog.ParseUtf8(File.ReadAllBytes(Path.Combine(folder, "src", "Rodaje.App", "Services", "Packages", "catalogo.json")), problems);
    }

    // «`src/Rodaje.App/Services/Packages/catalogo.json` viaja dentro del ejecutable y lista los complementos publicados
    // (transiciones, filtros, fuentes, títulos, zonas, looks de cámara, sonidos, preajustes profesionales, video 360 y los
    // seis módulos)»
    [Fact]
    public void The_catalog_lists_the_published_content_packs_and_the_six_modules()
    {
        var problems = new List<string>();
        PackageCatalog catalog = Bundled(problems);

        Assert.Empty(problems);
        foreach (string id in new[] { "rodaje-transiciones", "rodaje-filtros", "rodaje-fuentes", "rodaje-titulos", "rodaje-zonas",
            "rodaje-camaras", "rodaje-sonidos", "rodaje-preajustes-pro", "rodaje-360" })
        {
            Assert.Equal(PackageKind.Content, Assert.IsType<CatalogEntry>(catalog.Find(id)).Kind);
        }

        CatalogEntry[] modules = [.. catalog.Entries.Where(e => e.Id.StartsWith("modulo-", StringComparison.Ordinal))];
        Assert.Equal(6, modules.Length);
        Assert.All(modules, m => Assert.Equal(PackageKind.Module, m.Kind));
    }

    // «id, tipo, nombre, descripción, repositorio, programas y lo que trae (en un módulo, sus funciones)»
    [Fact]
    public void Each_entry_carries_id_kind_name_description_repository_apps_and_what_it_provides()
    {
        PackageCatalog catalog = PackageCatalog.Parse("""
            {
              "formatVersion": 1,
              "entries": [
                { "id": "modulo-ritmo", "kind": "module", "name": "Ritmo", "description": "Marca el pulso.",
                  "repository": "https://github.com/Dathinel/modulo-ritmo", "apps": ["rodaje"], "provides": ["pulso", "compas"] }
              ]
            }
            """);

        CatalogEntry entry = Assert.Single(catalog.Entries);
        Assert.Equal("modulo-ritmo", entry.Id);
        Assert.Equal(PackageKind.Module, entry.Kind);
        Assert.Equal("Ritmo", entry.Name);
        Assert.Equal("Marca el pulso.", entry.Description);
        Assert.Equal("https://github.com/Dathinel/modulo-ritmo", entry.Repository.AbsoluteUri);
        Assert.Equal(["rodaje"], entry.Apps);
        Assert.Equal(["pulso", "compas"], entry.Provides);

        Assert.All(Bundled().Entries, e =>
        {
            Assert.NotEmpty(e.Name);
            Assert.NotEmpty(e.Description);
            Assert.NotEmpty(e.Apps);
            Assert.NotEmpty(e.Provides);
        });
    }

    // «También nombra los programas (`rodaje`, y los editores de audio y de fotos como «en planes») para mostrar
    // «Funciona en».»
    [Fact]
    public void The_catalog_names_rodaje_and_the_planned_editors()
    {
        PackageCatalog catalog = Bundled();

        Assert.Equal("Rodaje", catalog.AppName("rodaje"));
        Assert.Equal("Editor de audio (en planes)", catalog.AppName("editor-audio"));
        Assert.Equal("Editor de fotos (en planes)", catalog.AppName("editor-fotos"));
    }

    // «para mostrar «Funciona en»»: cada programa que nombra una entrada tiene nombre en el catálogo.
    [Fact]
    public void Every_app_an_entry_works_in_has_a_name_to_show()
    {
        PackageCatalog catalog = Bundled();

        Assert.All(catalog.Entries.SelectMany(e => e.Apps), app => Assert.Contains(catalog.Apps, a => a.Id == app));
    }

    // «El catálogo solo informa: lo que se instala siempre pasa por la validación del manifiesto.»
    [Fact]
    public void A_package_with_a_catalog_id_still_needs_a_valid_manifest()
    {
        Assert.NotNull(Bundled().Find("rodaje-transiciones"));
        string archive = Samples.Zip(_folder.Combine("t.paquete"), ("paquete.json", Samples.Manifest()));

        PackageLocations locations = PackageLocations.Under(_folder.Combine("complementos"));

        Assert.Throws<PackageInstallException>(() => new PackageInstaller(locations, "rodaje").Install(archive));
        Assert.False(Directory.Exists(Path.Combine(locations.Packages, "rodaje-transiciones")));
    }
}
