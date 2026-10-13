namespace Rodaje.Packages.Tests;

public sealed class PackageLibraryTests : IDisposable
{
    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    [Fact]
    public void Scans_installed_bundled_and_own_and_reports_problems()
    {
        string bundled = _folder.Combine("exe", "paquetes");
        var locations = PackageLocations.Under(_folder.Combine("datos"), [bundled]);
        WritePackage(Path.Combine(bundled, "rodaje-transiciones"), "rodaje-transiciones", "1.0.0");
        WritePackage(Path.Combine(locations.Packages, "rodaje-transiciones"), "rodaje-transiciones", "1.2.0");
        WritePackage(Path.Combine(bundled, "rodaje-titulos"), "rodaje-titulos", "2.0.0", items: """[{ "id": "neon", "kind": "text-style", "name": "Neón" }]""");
        WritePackage(Path.Combine(locations.Packages, "para-fotos"), "para-fotos", "1.0.0", apps: "[\"editor-fotos\"]");
        Directory.CreateDirectory(Path.Combine(locations.Packages, "roto"));
        File.WriteAllText(Path.Combine(locations.Packages, "roto", "paquete.json"), "{");
        Directory.CreateDirectory(Path.Combine(locations.Packages, ".instalando-x"));
        Directory.CreateDirectory(Path.Combine(locations.Own, "transiciones"));
        File.WriteAllText(Path.Combine(locations.Own, "transiciones", "Mi Zoom Rápido.json"), """{ "kind": "transition", "name": "Mi zoom rápido" }""");
        File.WriteAllText(Path.Combine(locations.Own, "ignorado.json"), """{ "kind": "nada" }""");

        PackageScan scan = PackageLibrary.Scan(locations, "rodaje");

        InstalledPackage transitions = scan.Find("rodaje-transiciones")!;
        Assert.Equal("1.2.0", transitions.Manifest.Version.ToString());
        Assert.Equal(PackageOrigin.Installed, transitions.Origin);
        Assert.True(transitions.CanUninstall);
        Assert.Equal(PackageOrigin.Bundled, scan.Find("rodaje-titulos")!.Origin);
        Assert.False(scan.Find("rodaje-titulos")!.CanUninstall);
        Assert.Null(scan.Find("para-fotos"));
        Assert.Contains(scan.Problems, p => p.Message.Contains("editor-fotos", StringComparison.Ordinal));
        Assert.Contains(scan.Problems, p => p.Directory.EndsWith("roto", StringComparison.Ordinal));
        Assert.DoesNotContain(scan.Problems, p => p.Directory.Contains(".instalando", StringComparison.Ordinal));

        InstalledPackage own = scan.Find(PackageLibrary.OwnPackageId)!;
        Assert.Equal(PackageOrigin.Own, own.Origin);
        PackageItem mine = Assert.Single(own.Manifest.Items);
        Assert.Equal("mi-zoom-rapido", mine.Id);
        Assert.Equal("transiciones/Mi Zoom Rápido.json", mine.File);
        Assert.Equal(PackageOrigin.Own, scan.Packages[^1].Origin);

        PackageItemEntry[] items = [.. scan.Items(ItemKind.Transition)];
        Assert.Equal(["Deslizar", "Mi zoom rápido"], items.Select(i => i.Item.Name).Order());
        PackageItemEntry slide = items.Single(i => i.Item.Id == "deslizar");
        Assert.Equal("rodaje-transiciones@1.2.0/deslizar", slide.Reference.ToString());
        Assert.True(File.Exists(slide.FilePath));
    }

    [Fact]
    public void At_the_same_version_the_installed_copy_wins_over_the_bundled_one()
    {
        string bundled = _folder.Combine("exe", "paquetes");
        var locations = PackageLocations.Under(_folder.Combine("datos"), [bundled]);
        WritePackage(Path.Combine(bundled, "a"), "a", "1.0.0");
        WritePackage(Path.Combine(locations.Packages, "a"), "a", "1.0.0");

        Assert.Equal(PackageOrigin.Installed, PackageLibrary.Scan(locations, "rodaje").Find("a")!.Origin);
    }

    [Fact]
    public void Presets_saved_by_the_inspector_keep_their_name_as_item_id()
    {
        var locations = PackageLocations.Under(_folder.Combine("datos"));
        Directory.CreateDirectory(locations.Own);
        File.WriteAllText(Path.Combine(locations.Own, "zoom-de-entrada.json"), """
            { "format": "rodaje.preset", "version": 1, "id": "propios.zoom-de-entrada", "name": "Zoom de entrada", "kind": "transition",
              "effect": "rodaje.transform", "durationSeconds": 0.5, "tracks": {} }
            """);

        PackageItemEntry entry = Assert.Single(PackageLibrary.Scan(locations, "rodaje").Items(ItemKind.Transition));

        Assert.Equal("propios@0.0.0/zoom-de-entrada", entry.Reference.ToString());
        Assert.Equal("Zoom de entrada", entry.Item.Name);
    }

    [Fact]
    public void Missing_folders_give_an_empty_scan()
    {
        PackageScan scan = PackageLibrary.Scan(PackageLocations.Under(_folder.Combine("no-existe")), "rodaje");

        Assert.Empty(scan.Packages);
        Assert.Empty(scan.Problems);
    }

    [Theory]
    [InlineData("Mi Zoom Rápido", "mi-zoom-rapido")]
    [InlineData("  ¡Hola, Niño!  ", "hola-nino")]
    [InlineData("___", "elemento")]
    [InlineData("Transición 2", "transicion-2")]
    public void File_names_become_identifiers(string name, string expected)
    {
        string id = PackageLibrary.ToIdentifier(name);

        Assert.Equal(expected, id);
        Assert.True(Identifiers.IsValid(id));
    }

    /// <summary>
    /// La v1.0.0 publicada (sin ICU) derivaba «m-sica-pica-and» de «Música épica Ñandú.json»; ahora da «musica-epica-nandu».
    /// Las referencias que guardaron los proyectos con el id antiguo tienen que seguir encontrando el elemento.
    /// </summary>
    [Fact]
    public void Own_items_keep_the_legacy_id_of_v1_0_0_as_an_alias()
    {
        var locations = PackageLocations.Under(_folder.Combine("datos"));
        Directory.CreateDirectory(Path.Combine(locations.Own, "transiciones"));
        File.WriteAllText(Path.Combine(locations.Own, "transiciones", "Música épica Ñandú.json"), """{ "kind": "transition" }""");
        File.WriteAllText(Path.Combine(locations.Own, "transiciones", "Plana.json"), """{ "kind": "transition" }""");

        PackageManifest own = PackageLibrary.Scan(locations, "rodaje").Find(PackageLibrary.OwnPackageId)!.Manifest;

        Assert.Equal("m-sica-pica-and", PackageLibrary.LegacyIdentifier("Música épica Ñandú"));
        PackageItem item = own.FindItem("musica-epica-nandu", ItemKind.Transition)!;
        Assert.Equal("Música épica Ñandú", item.Name);
        Assert.Same(item, own.FindItem("m-sica-pica-and", ItemKind.Transition));
        Assert.Null(own.FindItem("m-sica-pica-and", ItemKind.Sound));
        Assert.Null(own.FindItem("plana")!.LegacyId);
        Assert.Equal("propios@0.0.0/musica-epica-nandu", own.ReferenceTo(item).ToString());

        using var store = new Rodaje.Packages.Content.PackageContentStore(PackageLibrary.Scan(locations, "rodaje"));
        foreach (string reference in new[] { "propios@0.0.0/m-sica-pica-and", "propios@0.0.0/otra-cosa" })
        {
            store.FindTransition(reference);
        }

        // El archivo no es una transición completa: el problema es de lectura, no de que falte el elemento.
        Assert.DoesNotContain("no trae", store.PresetProblem("propios@0.0.0/m-sica-pica-and") ?? "", StringComparison.Ordinal);
        Assert.Contains("no trae", store.PresetProblem("propios@0.0.0/otra-cosa") ?? "", StringComparison.Ordinal);
    }

    private static void WritePackage(string directory, string id, string version, string apps = "[\"rodaje\"]", string? items = null)
    {
        Directory.CreateDirectory(Path.Combine(directory, "transiciones"));
        File.WriteAllText(Path.Combine(directory, "transiciones", "deslizar.json"), "{}");
        File.WriteAllText(Path.Combine(directory, "paquete.json"), items is null
            ? Samples.Manifest(id, version, apps: apps)
            : Samples.Manifest(id, version, apps: apps, items: items));
    }
}

public sealed class PackageCatalogTests
{
    private const string Catalog = """
        {
          "formatVersion": 1,
          "apps": [{ "id": "rodaje", "name": "Rodaje" }, { "id": "editor-audio", "name": "Editor de audio", "planned": true }],
          "entries": [
            { "id": "modulo-ritmo", "kind": "module", "name": "Ritmo", "description": "d", "repository": "https://github.com/Dathinel/modulo-ritmo", "apps": ["rodaje", "editor-audio"], "provides": ["ritmo"] },
            { "id": "sin-repo", "name": "X" },
            { "id": "inseguro", "name": "X", "repository": "http://example.com" },
            { "id": "modulo-ritmo", "name": "Repetido", "repository": "https://github.com/Dathinel/otro" },
            { "id": "rodaje-titulos", "name": "Títulos", "repository": "https://github.com/Dathinel/rodaje-titulos" }
          ]
        }
        """;

    [Fact]
    public void Parses_entries_and_skips_the_broken_ones()
    {
        var problems = new List<string>();
        PackageCatalog catalog = PackageCatalog.Parse(Catalog, problems);

        Assert.Equal(["modulo-ritmo", "rodaje-titulos"], catalog.Entries.Select(e => e.Id));
        CatalogEntry rhythm = catalog.Find("modulo-ritmo")!;
        Assert.Equal(PackageKind.Module, rhythm.Kind);
        Assert.Equal(["rodaje", "editor-audio"], rhythm.Apps);
        Assert.Equal(PackageKind.Content, catalog.Find("rodaje-titulos")!.Kind);
        Assert.Equal(3, problems.Count);
        Assert.Equal("Rodaje", catalog.AppName("rodaje"));
        Assert.Equal("Editor de audio (en planes)", catalog.AppName("editor-audio"));
        Assert.Equal("otro-programa", catalog.AppName("otro-programa"));
    }

    [Fact]
    public void Rejects_what_is_not_a_catalog()
    {
        Assert.Throws<FormatException>(() => PackageCatalog.Parse("[]"));
        Assert.Throws<FormatException>(() => PackageCatalog.Parse("{"));
        Assert.Throws<FormatException>(() => PackageCatalog.Parse("""{ "formatVersion": 9, "entries": [] }"""));
    }

    [Fact]
    public async Task The_placeholder_downloader_says_it_is_not_available()
    {
        var downloader = new UnavailablePackageDownloader("Descarga desde GitHub: próximamente.");
        CatalogEntry entry = PackageCatalog.Parse(Catalog).Entries[0];

        Assert.False(downloader.IsAvailable);
        var ex = await Assert.ThrowsAsync<NotSupportedException>(() => downloader.DownloadAsync(entry, null, CancellationToken.None));
        Assert.Equal("Descarga desde GitHub: próximamente.", ex.Message);
    }
}
