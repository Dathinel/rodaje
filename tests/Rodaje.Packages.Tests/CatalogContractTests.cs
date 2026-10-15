namespace Rodaje.Packages.Tests;

/// <summary>Una prueba por promesa de la sección «Catálogo» de <c>docs/formato-paquete.md</c>, con su frase encima.</summary>
public sealed class CatalogContractTests : IDisposable
{
    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private static PackageCatalog Bundled()
    {
        var problems = new List<string>();
        PackageCatalog catalog = PackageCatalog.ParseUtf8(File.ReadAllBytes(BundledPath()), problems);
        Assert.Empty(problems);
        return catalog;
    }

    private static string BundledPath()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Rodaje.slnx")))
            {
                return Path.Combine(dir.FullName, "src", "Rodaje.App", "Services", "Packages", "catalogo.json");
            }
        }

        throw new DirectoryNotFoundException("No encuentro la raíz del repositorio.");
    }

    // «lista los complementos publicados (transiciones, filtros, fuentes, títulos, zonas, looks de cámara, sonidos,
    // preajustes profesionales, video 360 y los seis módulos)»
    [Fact]
    public void Lists_the_published_content_packages_and_the_six_modules()
    {
        PackageCatalog catalog = Bundled();

        string[] content = ["rodaje-transiciones", "rodaje-filtros", "rodaje-fuentes", "rodaje-titulos", "rodaje-zonas",
            "rodaje-camaras", "rodaje-sonidos", "rodaje-preajustes-pro", "rodaje-360"];
        string[] modules = ["modulo-subtitulos", "modulo-traducir", "modulo-voz", "modulo-audio-ia", "modulo-vision", "modulo-raw"];
        Assert.Equal(content.Order(), catalog.Entries.Where(e => e.Kind == PackageKind.Content).Select(e => e.Id).Order());
        Assert.All(modules, id => Assert.Equal(PackageKind.Module, Assert.IsType<CatalogEntry>(catalog.Find(id)).Kind));
        Assert.Equal(6, catalog.Entries.Count(e => e.Id.StartsWith("modulo-", StringComparison.Ordinal)));
    }

    // «id, tipo, nombre, descripción, repositorio, programas y lo que trae»
    [Fact]
    public void Every_entry_carries_id_kind_name_description_repository_apps_and_what_it_brings()
    {
        Assert.All(Bundled().Entries, e =>
        {
            Assert.True(Identifiers.IsValid(e.Id), e.Id);
            Assert.True(Enum.IsDefined(e.Kind), e.Id);
            Assert.False(string.IsNullOrWhiteSpace(e.Name), e.Id);
            Assert.False(string.IsNullOrWhiteSpace(e.Description), e.Id);
            Assert.Equal(Uri.UriSchemeHttps, e.Repository.Scheme);
            Assert.NotEmpty(e.Apps);
            Assert.NotEmpty(e.Provides);
        });
    }

    // «lo que trae (en un módulo, sus funciones)»: en el contenido, tipos de elemento del formato.
    [Fact]
    public void Content_entries_bring_element_kinds_of_the_format()
    {
        string[] kinds = ["lut", "filter", "effect", "transition", "text-style", "font", "safe-zones", "sound", "export-preset"];
        Assert.All(Bundled().Entries.Where(e => e.Kind == PackageKind.Content), e => Assert.All(e.Provides, p => Assert.Contains(p, kinds)));
    }

    // «lo que trae (en un módulo, sus funciones)»
    [Fact]
    public void A_module_entry_keeps_its_functions_in_order()
    {
        PackageCatalog catalog = PackageCatalog.Parse("""
            { "formatVersion": 1, "entries": [ { "id": "modulo-voz", "kind": "module", "name": "Voz", "description": "d",
              "repository": "https://github.com/Dathinel/modulo-voz", "apps": ["rodaje"], "provides": ["voces", "voz", "entregar"] } ] }
            """);

        CatalogEntry entry = Assert.Single(catalog.Entries);
        Assert.Equal(PackageKind.Module, entry.Kind);
        Assert.Equal(["voces", "voz", "entregar"], entry.Provides);
        Assert.Equal(["voces", "voz", "entregar"], Bundled().Find("modulo-voz")!.Provides);
    }

    // «También nombra los programas (`rodaje`, y los editores de audio y de fotos como «en planes») para mostrar «Funciona en».»
    [Fact]
    public void Names_rodaje_and_the_planned_editors()
    {
        PackageCatalog catalog = Bundled();

        Assert.Equal(["rodaje", "editor-audio", "editor-fotos"], catalog.Apps.Select(a => a.Id));
        Assert.False(catalog.Apps[0].Planned);
        Assert.Equal("Rodaje", catalog.AppName("rodaje"));
        Assert.Equal("Editor de audio (en planes)", catalog.AppName("editor-audio"));
        Assert.Equal("Editor de fotos (en planes)", catalog.AppName("editor-fotos"));
        Assert.All(catalog.Entries, e => Assert.All(e.Apps, app => Assert.Contains(app, catalog.Apps.Select(a => a.Id))));
    }

    // «El catálogo solo informa: lo que se instala siempre pasa por la validación del manifiesto.»
    [Fact]
    public void Being_in_the_catalog_does_not_skip_manifest_validation()
    {
        PackageCatalog catalog = Bundled();
        Assert.NotNull(catalog.Find("rodaje-transiciones"));
        PackageLocations locations = PackageLocations.Under(_folder.Combine("complementos"));
        var installer = new PackageInstaller(locations, "rodaje");
        string archive = Samples.Zip(_folder.Combine("t.paquete"),
            ("paquete.json", Samples.Manifest(version: "no-es-version")), ("transiciones/deslizar.json", "{}"));

        Assert.ThrowsAny<PackageInstallException>(() => installer.Install(archive));
        Assert.False(Directory.Exists(Path.Combine(locations.Packages, "rodaje-transiciones")));
    }

    // «El catálogo solo informa»: una entrada mal escrita se omite sin tumbar el resto.
    [Fact]
    public void A_broken_entry_is_reported_and_the_rest_still_reads()
    {
        var problems = new List<string>();
        PackageCatalog catalog = PackageCatalog.Parse("""
            { "formatVersion": 1, "entries": [
              { "id": "rodaje-zonas", "name": "Zonas", "repository": "https://github.com/Dathinel/rodaje-zonas" },
              { "id": "Mal Id", "name": "X", "repository": "https://github.com/Dathinel/x" },
              { "id": "rodaje-raro", "kind": "plugin", "name": "X", "repository": "https://github.com/Dathinel/x" } ] }
            """, problems);

        Assert.Equal(["rodaje-zonas"], catalog.Entries.Select(e => e.Id));
        Assert.Equal(2, problems.Count);
    }
}
