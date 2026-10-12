using System.IO.Compression;
using Rodaje.App.Services;
using Rodaje.App.Services.Packages;
using Rodaje.App.Tests.Support;
using Rodaje.Core.Model;
using Rodaje.Packages;

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
            ["modulo-audio-ia", "modulo-raw", "modulo-subtitulos", "modulo-traducir", "modulo-vision", "modulo-voz",
             "rodaje-filtros", "rodaje-fuentes", "rodaje-titulos", "rodaje-transiciones", "rodaje-zonas"],
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

    /// <summary>
    /// Cada módulo de <c>modules\</c> (menos el eco de pruebas) está en el catálogo con su id, sus funciones y su
    /// repositorio, va en la versión 1.0.0 con el exe en la raíz (los modelos van en <c>&lt;carpeta&gt;\modelos</c>, junto
    /// a él) y cada modelo se baja de un origen permitido: Hugging Face por commit o una release de Dathinel.
    /// </summary>
    [Fact]
    public void Every_module_in_the_repository_is_in_the_catalog_with_downloadable_models()
    {
        PackageCatalog catalog = PackageService.LoadBundledCatalog();
        string modules = Path.Combine(RepositoryRoot(), "modules");
        int count = 0;
        foreach (string manifest in Directory.GetFiles(modules, "paquete.json", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            ManifestResult read = ManifestReader.ReadFile(manifest, checkFiles: false);
            PackageManifest m = Assert.IsType<PackageManifest>(read.Manifest);
            if (m.Id == "rodaje-eco")
            {
                continue;
            }

            count++;
            CatalogEntry entry = Assert.IsType<CatalogEntry>(catalog.Find(m.Id));
            Assert.Equal(PackageKind.Module, entry.Kind);
            Assert.Matches(@"^1\.\d+\.\d+$", m.Version.ToString());
            Assert.Equal(m.Module!.Functions.Order(), entry.Provides.Order());
            Assert.Equal(entry.Repository, m.Repository);
            Assert.DoesNotContain('/', m.Module.Entry);
            foreach (Rodaje.Packages.Download.ModuleModel model in Rodaje.Packages.Download.ModuleModels.Read(manifest))
            {
                Assert.StartsWith("modelos/", model.File, StringComparison.Ordinal);
                Assert.True(Rodaje.Packages.Download.DownloadUrlPolicy.Default.IsAllowed(model.Url, out string? reason), $"{m.Id}/{model.Id}: {reason}");
            }
        }

        Assert.Equal(6, count);
    }

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Rodaje.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException("No encuentro la raíz del repositorio.");
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
        Assert.False(entries.Single(e => e.Id == "modulo-audio-ia").IsInstalled);
        Assert.Contains("Editor de audio (en planes)", entries.Single(e => e.Id == "modulo-audio-ia").WorksIn);
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

        Assert.Contains("Instalar desde archivo", ex.Message, StringComparison.Ordinal);
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

    [Fact]
    public void Installing_from_the_catalog_downloads_installs_and_deletes_the_download() => UiThread.Run(async () =>
    {
        string archive = Path.Combine(_folder.Path, "descargado.paquete");
        WriteArchive(archive, "rodaje-filtros", "filter");
        var downloader = new FakeDownloader(archive, InstallerHash(archive));
        PackageService service = new(PackageLocations.Under(Path.Combine(_folder.Path, "complementos")), PackageService.LoadBundledCatalog(), downloader);

        InstallResult result = await service.InstallFromCatalogAsync(service.Catalog.Find("rodaje-filtros")!);

        Assert.Equal("rodaje-filtros", result.Package.Id);
        Assert.False(File.Exists(archive));
        Assert.True(service.Entries().Single(e => e.Id == "rodaje-filtros").IsInstalled);
    });

    [Fact]
    public void A_download_whose_hash_does_not_match_is_deleted_and_nothing_is_installed() => UiThread.Run(async () =>
    {
        string archive = Path.Combine(_folder.Path, "descargado.paquete");
        WriteArchive(archive, "rodaje-filtros", "filter");
        PackageService service = new(PackageLocations.Under(Path.Combine(_folder.Path, "complementos")), PackageService.LoadBundledCatalog(),
            new FakeDownloader(archive, new string('0', 64)));

        await Assert.ThrowsAsync<PackageInstallException>(() => service.InstallFromCatalogAsync(service.Catalog.Find("rodaje-filtros")!));

        Assert.False(File.Exists(archive));
        Assert.False(service.Entries().Single(e => e.Id == "rodaje-filtros").IsInstalled);
    });

    [Fact]
    public void A_newer_published_version_offers_an_update() => UiThread.Run(async () =>
    {
        string archive = Path.Combine(_folder.Path, "f.paquete");
        WriteArchive(archive, "rodaje-filtros", "filter");
        var downloader = new FakeDownloader(archive, null) { Versions = new() { ["rodaje-filtros"] = PackageVersion.Parse("1.2.0") } };
        PackageService service = new(PackageLocations.Under(Path.Combine(_folder.Path, "complementos")), PackageService.LoadBundledCatalog(), downloader);
        await service.InstallFromFileAsync(archive);
        Assert.False(service.HasUpdate(service.Entries().Single(e => e.Id == "rodaje-filtros")));

        await service.AvailableVersionsAsync();

        Assert.True(service.HasUpdate(service.Entries().Single(e => e.Id == "rodaje-filtros")));
        Assert.False(service.HasUpdate(service.Entries().Single(e => e.Id == "rodaje-titulos")));
        Assert.Null(service.UpdatesProblem);
    });

    [Fact]
    public void The_app_downloader_needs_the_catalog_key()
    {
        PackageLocations locations = PackageLocations.Under(Path.Combine(_folder.Path, "complementos"));
        Assert.False(Rodaje.App.Services.Packages.Download.CatalogSource.CreateDownloader(locations, publicKey: "").IsAvailable);
        using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        IPackageDownloader downloader = Rodaje.App.Services.Packages.Download.CatalogSource.CreateDownloader(locations, Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
        Assert.True(downloader.IsAvailable);
        (downloader as IDisposable)?.Dispose();
        Assert.EndsWith(Path.Combine("complementos", "descargas"), Rodaje.App.Services.Packages.Download.CatalogSource.DownloadFolder(locations), StringComparison.Ordinal);
    }

    private static string InstallerHash(string path) => PackageInstaller.ComputeSha256(path);

    private sealed class FakeDownloader(string path, string? sha) : IPackageDownloader
    {
        public Dictionary<string, PackageVersion> Versions { get; init; } = [];

        public bool IsAvailable => true;

        public string? UnavailableReason => null;

        public Task<DownloadedPackage> DownloadAsync(CatalogEntry entry, IProgress<double>? progress, CancellationToken cancellationToken)
        {
            progress?.Report(1);
            return Task.FromResult(new DownloadedPackage(path, sha));
        }

        public Task<IReadOnlyDictionary<string, PackageVersion>> AvailableVersionsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, PackageVersion>>(Versions);
    }

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
