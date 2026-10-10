using System.IO.Compression;
using Rodaje.App.Services.Packages;
using Rodaje.App.Tests.Support;
using Rodaje.Core.Model;
using Rodaje.Paquetes;

namespace Rodaje.App.Tests.Packages;

public sealed class PackageServiceTests : IDisposable
{
    private readonly TempDirectory _folder = new();

    public void Dispose() => _folder.Dispose();

    [Fact]
    public void The_bundled_catalog_lists_the_approved_repositories()
    {
        var problems = new List<string>();
        PackageCatalog catalog = PackageService.LoadBundledCatalog(problems);

        Assert.Empty(problems);
        Assert.Equal(
            ["modulo-quitar-fondo", "modulo-ritmo", "modulo-silencios", "modulo-subtitulos", "rodaje-titulos", "rodaje-transiciones"],
            catalog.Entries.Select(e => e.Id).Order());
        Assert.All(catalog.Entries, e =>
        {
            Assert.Equal($"https://github.com/Dathinel/{e.Id}", e.Repository.AbsoluteUri);
            Assert.Contains(PackageService.AppId, e.Apps);
            Assert.False(string.IsNullOrWhiteSpace(e.Description));
            Assert.Equal(e.Id.StartsWith("modulo-", StringComparison.Ordinal) ? PackageKind.Module : PackageKind.Content, e.Kind);
        });
        Assert.Equal("Editor de audio (en planes)", catalog.AppName("editor-audio"));
    }

    [Fact]
    public void Entries_merge_the_catalog_with_what_is_installed() => UiThread.Run(async () =>
    {
        PackageService service = Create();
        string archive = Path.Combine(_folder.Path, "titulos.paquete");
        WriteArchive(archive, "rodaje-titulos", "text-style");
        WriteArchive(Path.Combine(_folder.Path, "extra.paquete"), "paquete-suelto", "sound");
        int changes = 0;
        service.Changed += (_, _) => changes++;

        InstallResult result = await service.InstallFromFileAsync(archive);
        await service.InstallFromFileAsync(Path.Combine(_folder.Path, "extra.paquete"));
        IReadOnlyList<PluginEntry> entries = service.Entries();

        Assert.Equal(InstallOutcome.Installed, result.Outcome);
        Assert.Equal(2, changes);
        PluginEntry titles = entries.Single(e => e.Id == "rodaje-titulos");
        Assert.True(titles.IsInstalled);
        Assert.True(titles.CanUninstall);
        Assert.Equal("1.0.0", titles.InstalledVersion);
        Assert.NotNull(titles.Catalog);
        PluginEntry loose = entries.Single(e => e.Id == "paquete-suelto");
        Assert.Null(loose.Catalog);
        Assert.False(entries.Single(e => e.Id == "modulo-ritmo").IsInstalled);
        Assert.Contains("Editor de audio (en planes)", entries.Single(e => e.Id == "modulo-ritmo").WorksIn);
        Assert.Equal(PackageKind.Content, entries[0].Kind);
        Assert.Equal(PackageKind.Module, entries[^1].Kind);

        await service.UninstallAsync("rodaje-titulos");
        Assert.False(service.Entries().Single(e => e.Id == "rodaje-titulos").IsInstalled);
    });

    [Fact]
    public void Installing_from_the_catalog_says_downloads_come_later() => UiThread.Run(async () =>
    {
        PackageService service = Create();
        CatalogEntry entry = service.Catalog.Entries[0];

        var ex = await Assert.ThrowsAsync<NotSupportedException>(() => service.InstallFromCatalogAsync(entry));

        Assert.Contains("próximamente", ex.Message, StringComparison.Ordinal);
        Assert.False(service.Downloader.IsAvailable);
    });

    [Fact]
    public void Library_categories_show_engine_items_then_package_and_own_items() => UiThread.Run(async () =>
    {
        PackageService service = Create();
        WriteArchive(Path.Combine(_folder.Path, "t.paquete"), "rodaje-transiciones", "transition");
        await service.InstallFromFileAsync(Path.Combine(_folder.Path, "t.paquete"));
        Directory.CreateDirectory(service.Locations.Own);
        await File.WriteAllTextAsync(Path.Combine(service.Locations.Own, "Mi barrido.json"), """{ "kind": "transition", "name": "Mi barrido" }""");
        await service.RefreshAsync();

        IReadOnlyList<LibraryElement> transitions = LibraryElements.For(LibraryCategory.Transitions, service.Installed);
        IReadOnlyList<LibraryElement> effects = LibraryElements.For(LibraryCategory.Effects, service.Installed);

        Assert.Equal(["Fundido cruzado", "Fundido a negro", "Elemento", "Mi barrido"], transitions.Select(t => t.Name));
        Assert.Equal("rodaje-transiciones@1.0.0/elemento", transitions[2].Reference);
        Assert.Equal("Mío", transitions[3].Source);
        Assert.Equal(PackageOrigin.Own, transitions[3].Origin);
        Assert.Equal("propios@0.0.0/mi-barrido", transitions[3].Reference);
        Assert.Equal(BuiltInEffects.Transform, effects[0].Reference);
        Assert.All(effects, e => Assert.True(e.IsAvailable));
        Assert.Equal("Mis transiciones", LibraryElements.OwnTitle(LibraryCategory.Transitions));
        Assert.Empty(LibraryElements.For(LibraryCategory.Ai, service.Installed));
        Assert.Equal([ItemKind.Filter, ItemKind.Lut], LibraryElements.KindsOf(LibraryCategory.Filters));
    });

    private PackageService Create() =>
        new(PackageLocations.Under(Path.Combine(_folder.Path, "complementos")), PackageService.LoadBundledCatalog());

    private static void WriteArchive(string path, string id, string kind)
    {
        using var stream = new FileStream(path, FileMode.Create);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        using var writer = new StreamWriter(zip.CreateEntry("paquete.json").Open());
        writer.Write($$"""
            {
              "formatVersion": 1, "id": "{{id}}", "version": "1.0.0", "name": "{{id}}", "author": "Dathinel", "license": "MIT",
              "apps": ["rodaje"], "items": [{ "id": "elemento", "kind": "{{kind}}", "name": "Elemento" }]
            }
            """);
    }
}
