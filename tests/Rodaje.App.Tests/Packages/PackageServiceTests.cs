using System.IO.Compression;
using Rodaje.App.Services;
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

        Assert.Equal(
            ["Fundido cruzado", "Fundido a color", "Barrido", "Empujar", "Fundido con zoom", "Fundido con desenfoque", "Elemento", "Mi barrido"],
            transitions.Select(t => t.Name));
        Assert.Equal("rodaje-transiciones@1.0.0/elemento", transitions[6].Reference);
        Assert.Equal("Mío", transitions[7].Source);
        Assert.Equal(PackageOrigin.Own, transitions[7].Origin);
        Assert.Equal("propios@0.0.0/mi-barrido", transitions[7].Reference);
        Assert.All(transitions, t => Assert.Equal(ItemKind.Transition, t.Kind));
        Assert.Equal(BuiltInEffects.Transform, effects[0].Reference);
        Assert.All(effects, e => Assert.True(e.IsAvailable));
        Assert.Equal("Mis transiciones", LibraryElements.OwnTitle(LibraryCategory.Transitions));
        Assert.Empty(LibraryElements.For(LibraryCategory.Ai, service.Installed));
        Assert.Equal([ItemKind.Filter, ItemKind.Lut], LibraryElements.KindsOf(LibraryCategory.Filters));
    });

    [Fact]
    public void Refreshing_during_an_install_does_not_delete_the_extraction_in_progress() => UiThread.Run(async () =>
    {
        PackageService service = Create();
        await service.RefreshAsync();
        string inProgress = Path.Combine(service.Locations.Packages, ".instalando-0123456789ab");
        Directory.CreateDirectory(inProgress);

        await service.RefreshAsync();

        Assert.True(Directory.Exists(inProgress));
    });

    [Fact]
    public void Leftovers_of_a_blackout_are_resolved_before_the_first_operation() => UiThread.Run(async () =>
    {
        PackageService service = Create();
        string leftover = Path.Combine(service.Locations.Packages, ".instalando-0123456789ab");
        Directory.CreateDirectory(leftover);

        await service.RefreshAsync();

        Assert.False(Directory.Exists(leftover));
    });

    [Fact]
    public void Simultaneous_installs_refreshes_and_uninstalls_run_one_at_a_time() => UiThread.Run(async () =>
    {
        PackageService service = Create();
        string archive = Path.Combine(_folder.Path, "t.paquete");
        WriteArchive(archive, "rodaje-transiciones", "transition", extraFiles: 100);

        var operations = new List<Task>();
        for (int i = 0; i < 6; i++)
        {
            operations.Add(service.InstallFromFileAsync(archive));
            operations.Add(service.RefreshAsync());
        }

        await Task.WhenAll(operations);
        Task<InstallResult>[] installs = [.. operations.OfType<Task<InstallResult>>()];
        Assert.Equal(1, installs.Count(t => t.Result.Outcome == InstallOutcome.Installed));
        Assert.Equal(5, installs.Count(t => t.Result.Outcome == InstallOutcome.Reinstalled));
        Assert.Equal(["rodaje-transiciones"], Directory.GetDirectories(service.Locations.Packages).Select(Path.GetFileName));

        await Task.WhenAll(service.UninstallAsync("rodaje-transiciones"), service.RefreshAsync());
        Assert.Empty(Directory.GetDirectories(service.Locations.Packages));
        Assert.Null(service.Installed.Find("rodaje-transiciones"));
    });

    [Fact]
    public void With_another_instance_open_nothing_installed_is_removed_or_replaced() => UiThread.Run(async () =>
    {
        string instances = Path.Combine(_folder.Path, "instancias");
        using InstanceRegistry mine = InstanceRegistry.Register(instances);
        PackageService service = Create(mine);
        string archive = Path.Combine(_folder.Path, "t.paquete");
        WriteArchive(archive, "rodaje-transiciones", "transition");
        await service.InstallFromFileAsync(archive);
        string leftover = Path.Combine(service.Locations.Packages, ".instalando-0123456789ab");

        InstanceRegistry other = InstanceRegistry.Register(instances);
        Directory.CreateDirectory(leftover);
        Assert.Equal(2, service.ActiveInstances());
        var uninstall = await Assert.ThrowsAsync<PackageInstallException>(() => service.UninstallAsync("rodaje-transiciones"));
        var replace = await Assert.ThrowsAsync<PackageInstallException>(() => service.InstallFromFileAsync(archive));
        WriteArchive(Path.Combine(_folder.Path, "nuevo.paquete"), "paquete-nuevo", "sound");
        InstallResult added = await service.InstallFromFileAsync(Path.Combine(_folder.Path, "nuevo.paquete"));

        Assert.Equal(PackageService.OtherInstancesMessage, uninstall.Message);
        Assert.Contains("Cierra las otras ventanas", replace.Message, StringComparison.Ordinal);
        Assert.Equal(InstallOutcome.Installed, added.Outcome);
        Assert.NotNull(service.Installed.Find("rodaje-transiciones"));
        Assert.True(Directory.Exists(leftover));

        other.Dispose();
        await service.UninstallAsync("rodaje-transiciones");
        Assert.Null(service.Installed.Find("rodaje-transiciones"));
    });

    [Fact]
    public void Leftovers_wait_until_this_is_the_only_instance() => UiThread.Run(async () =>
    {
        string instances = Path.Combine(_folder.Path, "instancias");
        using InstanceRegistry mine = InstanceRegistry.Register(instances);
        InstanceRegistry first = InstanceRegistry.Register(instances);
        PackageService service = Create(mine);
        string extracting = Path.Combine(service.Locations.Packages, ".instalando-0123456789ab");
        Directory.CreateDirectory(extracting);

        await service.RefreshAsync();
        Assert.True(Directory.Exists(extracting));

        first.Dispose();
        await service.RefreshAsync();
        Assert.False(Directory.Exists(extracting));
    });

    private PackageService Create(InstanceRegistry? instances = null) =>
        new(PackageLocations.Under(Path.Combine(_folder.Path, "complementos")), PackageService.LoadBundledCatalog(), instances: instances);

    private static void WriteArchive(string path, string id, string kind, int extraFiles = 0)
    {
        using var stream = new FileStream(path, FileMode.Create);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        for (int i = 0; i < extraFiles; i++)
        {
            using var extra = new StreamWriter(zip.CreateEntry($"datos/{i}.json").Open());
            extra.Write("{}");
        }

        using var writer = new StreamWriter(zip.CreateEntry("paquete.json").Open());
        writer.Write($$"""
            {
              "formatVersion": 1, "id": "{{id}}", "version": "1.0.0", "name": "{{id}}", "author": "Dathinel", "license": "MIT",
              "apps": ["rodaje"], "items": [{ "id": "elemento", "kind": "{{kind}}", "name": "Elemento" }]
            }
            """);
    }
}
