using System.IO.Compression;
using Rodaje.App.Services.Modules;
using Rodaje.App.Services.Packages;
using Rodaje.App.Tests.Modules;
using Rodaje.App.Tests.Support;
using Rodaje.Modules.Protocol;
using Rodaje.Packages;
using Rodaje.Packages.Download;
using Rodaje.TestServer;

namespace Rodaje.App.Tests.Packages;

/// <summary>
/// Un módulo instalado desde el catálogo: sus modelos se bajan al almacén (hash y reanudación) y quedan en su carpeta
/// donde el módulo los busca; desinstalar o actualizar un módulo abierto lo cierra antes; los ids viejos se migran.
/// </summary>
public sealed class ModuleInstallTests : IAsyncLifetime, IDisposable
{
    private static readonly string[] EchoFiles =
        ["Rodaje.Module.Echo.exe", "Rodaje.Module.Echo.dll", "Rodaje.Module.Echo.runtimeconfig.json", "Rodaje.Module.Echo.deps.json", "Rodaje.Modules.Protocol.dll"];

    private const string LongCount = """{"pasos":100000,"msPorPaso":10}""";

    private readonly TempDirectory _folder = new();
    private readonly TestHttpServer _server = TestHttpServer.Start();
    private readonly HttpClient _http = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        Assert.Empty(_server.Faults);
    }

    public void Dispose()
    {
        _http.Dispose();
        for (int i = 0; ; i++)
        {
            try
            {
                _folder.Dispose();
                return;
            }
            catch (Exception ex) when (i < 30 && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(100);
            }
        }
    }

    private PackageLocations Locations => PackageLocations.Under(Path.Combine(_folder.Path, "complementos"));

    private string StoreFolder => Path.Combine(_folder.Path, "complementos", "modelos");

    private PackageService Service(string archive, int maxAttempts = 5) =>
        new(Locations, PackageService.LoadBundledCatalog(), new SingleFileDownloader(archive),
            models: new ModelStore(StoreFolder, new FileDownloader(_http) { MaxAttempts = maxAttempts, RetryDelay = TimeSpan.Zero }, DownloadUrlPolicy.WithLoopback("Dathinel")));

    private static CatalogEntry Entry(PackageService service) => service.Catalog.Find("modulo-voz")!;

    [Fact]
    public void Installing_a_module_downloads_its_models_into_its_folder() => UiThread.Run(async () =>
    {
        byte[] voice = TestPayloads.Deterministic(300_000, seed: 3);
        byte[] lexicon = TestPayloads.Deterministic(50_000, seed: 4);
        string archive = WriteModule("modulo-voz", "1.0.0", Model("voz", "modelos/voz/kokoro.onnx", voice), Model("lexico", "modelos/voz/misaki/us_gold.json", lexicon));
        PackageService service = Service(archive);
        var stages = new List<InstallProgress>();

        InstallResult result = await service.InstallFromCatalogAsync(Entry(service), new DirectProgressForTests(stages.Add));

        string module = result.Package.Directory;
        Assert.Equal(voice, File.ReadAllBytes(Path.Combine(module, "modelos", "voz", "kokoro.onnx")));
        Assert.Equal(lexicon, File.ReadAllBytes(Path.Combine(module, "modelos", "voz", "misaki", "us_gold.json")));
        Assert.True(File.Exists(Path.Combine(StoreFolder, TestPayloads.Sha256Hex(voice))));
        Assert.Empty(service.MissingModelsOf("modulo-voz"));
        Assert.Contains(stages, p => p.Stage.StartsWith("Modelo 1 de 2: voz", StringComparison.Ordinal) && p.Fraction == 1);
        Assert.Contains(stages, p => p.Stage.StartsWith("Modelo 2 de 2: lexico", StringComparison.Ordinal));

        // Reinstalar no vuelve a bajar nada: los modelos ya están en el almacén.
        int requests = _server.Requests.Count;
        await service.InstallFromCatalogAsync(Entry(service));
        Assert.Equal(requests, _server.Requests.Count);
        Assert.Equal(voice, File.ReadAllBytes(Path.Combine(module, "modelos", "voz", "kokoro.onnx")));
    });

    [Fact]
    public void A_cut_model_download_leaves_the_module_installed_and_resumes_where_it_stopped() => UiThread.Run(async () =>
    {
        byte[] voice = TestPayloads.Deterministic(400_000, seed: 5);
        string archive = WriteModule("modulo-voz", "1.0.0", Model("voz", "modelos/voz/kokoro.onnx", voice, cutAfter: 150_000));
        PackageService service = Service(archive, maxAttempts: 1);

        var pending = await Assert.ThrowsAsync<ModelsPendingException>(() => service.InstallFromCatalogAsync(Entry(service)));
        Assert.Equal("modulo-voz", pending.Result.Package.Id);
        Assert.True(service.Entries().Single(e => e.Id == "modulo-voz").IsInstalled);
        Assert.Single(service.MissingModelsOf("modulo-voz"));
        long partial = new FileInfo(Path.Combine(StoreFolder, TestPayloads.Sha256Hex(voice) + FileDownloader.PartialSuffix)).Length;
        Assert.InRange(partial, 0, voice.Length - 1);

        await service.EnsureModelsAsync("modulo-voz");

        Assert.Empty(service.MissingModelsOf("modulo-voz"));
        Assert.Equal(voice, File.ReadAllBytes(Path.Combine(pending.Result.Package.Directory, "modelos", "voz", "kokoro.onnx")));
        Assert.Equal(partial > 0 ? $"bytes={partial}-" : null, _server.Requests[^1].Range);
    });

    [Fact]
    public void A_model_with_a_wrong_hash_is_not_placed() => UiThread.Run(async () =>
    {
        byte[] voice = TestPayloads.Deterministic(10_000, seed: 6);
        string archive = WriteModule("modulo-voz", "1.0.0", Model("voz", "modelos/voz/kokoro.onnx", voice, served: TestPayloads.Corrupt(voice, 500)));
        PackageService service = Service(archive);

        await Assert.ThrowsAsync<ModelsPendingException>(() => service.InstallFromCatalogAsync(Entry(service)));

        Assert.False(File.Exists(Path.Combine(Locations.Modules, "modulo-voz", "modelos", "voz", "kokoro.onnx")));
        Assert.Empty(Directory.EnumerateFiles(StoreFolder));
    });

    [Fact]
    public void Uninstalling_or_updating_a_module_in_use_closes_it_first() => UiThread.Run(async () =>
    {
        byte[] voice = TestPayloads.Deterministic(1000, seed: 7);
        string v1 = WriteModule("modulo-voz", "1.0.0", Model("voz", "modelos/voz/kokoro.onnx", voice));
        PackageService service = Service(v1);
        await using ModuleManager manager = new(new ModuleManagerOptions { Roots = [new ModuleRoot(Locations.Modules, ModuleOrigin.Installed)] });
        service.CloseModule = manager.CloseModuleAsync;
        await service.InstallFromCatalogAsync(Entry(service));
        manager.Refresh();

        // Actualizar mientras trabaja: el trabajo se cancela, el proceso se cierra y la versión nueva queda en su sitio.
        Task<ModuleResult> running = manager.InvokeAsync("modulo-voz", "contar", EchoModules.Json(LongCount), "Contar");
        await WaitUntil(() => manager.ProcessIds.Count == 1 && manager.Jobs.Jobs[0].Progress > 0);
        int first = manager.ProcessIds[0];
        string v2 = WriteModule("modulo-voz", "1.1.0", Model("voz", "modelos/voz/kokoro.onnx", voice));
        InstallResult updated = await service.InstallFromFileAsync(v2);
        Assert.Equal(InstallOutcome.Updated, updated.Outcome);
        await Assert.ThrowsAnyAsync<Exception>(() => running);
        Assert.True(EchoModules.HasExited(first));
        Assert.Single(service.MissingModelsOf("modulo-voz"));

        // Desinstalar con el módulo abierto (ocioso): se cierra, la carpeta se va y el modelo deja el almacén.
        manager.Refresh();
        await service.EnsureModelsAsync("modulo-voz");
        ModuleResult again = await manager.InvokeAsync("modulo-voz", "eco", EchoModules.Json("""{"a":1}"""), "Eco");
        Assert.NotNull(again);
        int second = Assert.Single(manager.ProcessIds);

        await service.UninstallAsync("modulo-voz");

        Assert.True(EchoModules.HasExited(second));
        Assert.False(Directory.Exists(Path.Combine(Locations.Modules, "modulo-voz")));
        Assert.Empty(Directory.EnumerateFiles(StoreFolder));
    });

    [Fact]
    public void Modules_installed_with_an_old_id_are_migrated() => UiThread.Run(async () =>
    {
        string old = Path.Combine(Locations.Modules, "rodaje-vision");
        Directory.CreateDirectory(old);
        File.WriteAllText(Path.Combine(old, "Rodaje.Module.Vision.exe"), "");
        File.WriteAllText(Path.Combine(old, PackageManifest.FileName), """
            { "formatVersion": 1, "id": "rodaje-vision", "version": "0.1.0", "kind": "module", "name": "Visión", "author": "Dathinel",
              "license": "GPL-3.0-only", "apps": ["rodaje"], "description": "Quita el fondo «sin conexión»",
              "module": { "entry": "Rodaje.Module.Vision.exe", "protocol": 1, "functions": ["quitar-fondo"] } }
            """);
        string stale = Path.Combine(Locations.Modules, "rodaje-raw");
        Directory.CreateDirectory(stale);
        Directory.CreateDirectory(Path.Combine(Locations.Modules, "modulo-raw"));

        PackageService service = new(Locations, PackageService.LoadBundledCatalog());
        await service.RefreshAsync();

        Assert.False(Directory.Exists(old));
        Assert.False(Directory.Exists(stale));
        InstalledPackage migrated = service.Installed.Find("modulo-vision")!;
        Assert.Equal("Visión", migrated.Manifest.Name);
        Assert.Equal("Quita el fondo «sin conexión»", migrated.Manifest.Description);
        Assert.Equal(Path.Combine(Locations.Modules, "modulo-vision"), migrated.Directory);
    });

    private sealed record ServedModel(string Id, string File, byte[] Content, byte[] Served, long? CutAfter);

    private static ServedModel Model(string id, string file, byte[] content, long? cutAfter = null, byte[]? served = null) =>
        new(id, file, content, served ?? content, cutAfter);

    /// <summary>Un .paquete con el módulo eco bajo <paramref name="id"/> y sus modelos servidos por el servidor de prueba.</summary>
    private string WriteModule(string id, string version, params ServedModel[] models)
    {
        string models_ = string.Join(",", models.Select(m =>
        {
            string path = $"modelos/{version}/{m.Id}";
            if (m.CutAfter is { } cut)
            {
                _server.Map(path, TestResponse.File(m.Served) with { CutAfterBytes = cut }, TestResponse.File(m.Served));
            }
            else
            {
                _server.Map(path, TestResponse.File(m.Served));
            }

            return $$"""{ "id": "{{m.Id}}", "file": "{{m.File}}", "size": {{m.Content.Length}}, "sha256": "{{TestPayloads.Sha256Hex(m.Content)}}", "license": "MIT", "url": "{{_server.UrlFor(path)}}" }""";
        }));
        string archive = Path.Combine(_folder.Path, $"{id}-{version}.paquete");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            foreach (string file in EchoFiles)
            {
                zip.CreateEntryFromFile(Path.Combine(AppContext.BaseDirectory, file), file);
            }

            using var writer = new StreamWriter(zip.CreateEntry(PackageManifest.FileName).Open());
            writer.Write($$"""
                { "formatVersion": 1, "id": "{{id}}", "version": "{{version}}", "kind": "module", "name": "Eco como {{id}}", "author": "Pruebas",
                  "license": "GPL-3.0-only", "apps": ["rodaje"],
                  "module": { "entry": "Rodaje.Module.Echo.exe", "protocol": 1, "functions": ["eco", "contar"], "vramMb": 0, "models": [ {{models_}} ] } }
                """);
        }

        return archive;
    }

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 45_000)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(watch.ElapsedMilliseconds < timeoutMs, "No se cumplió a tiempo.");
            await Task.Delay(20);
        }
    }

    /// <summary>Devuelve siempre el mismo archivo, copiado (el servicio borra lo descargado tras instalar).</summary>
    private sealed class SingleFileDownloader(string archive) : IPackageDownloader
    {
        public bool IsAvailable => true;

        public string? UnavailableReason => null;

        public Task<DownloadedPackage> DownloadAsync(CatalogEntry entry, IProgress<double>? progress, CancellationToken cancellationToken)
        {
            string copy = archive + "." + Guid.NewGuid().ToString("N") + ".descarga";
            File.Copy(archive, copy);
            progress?.Report(1);
            return Task.FromResult(new DownloadedPackage(copy, PackageInstaller.ComputeSha256(copy)));
        }

        public Task<IReadOnlyDictionary<string, PackageVersion>> AvailableVersionsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, PackageVersion>>(new Dictionary<string, PackageVersion>());
    }

    private sealed class DirectProgressForTests(Action<InstallProgress> report) : IProgress<InstallProgress>
    {
        public void Report(InstallProgress value) => report(value);
    }
}
