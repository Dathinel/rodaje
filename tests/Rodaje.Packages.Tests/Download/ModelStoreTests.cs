using Rodaje.Packages.Download;
using Rodaje.TestServer;

namespace Rodaje.Packages.Tests.Download;

public sealed class ModelStoreTests : IAsyncLifetime, IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly TestHttpServer _server = TestHttpServer.Start();
    private readonly HttpClient _http = new();

    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>Cierra los servidores y comprueba que no tuvieron fallos propios (xunit 2 la llama antes de <see cref="Dispose"/>).</summary>
    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        Assert.Empty(_server.Faults);
    }

    public void Dispose()
    {
        _http.Dispose();
        _temp.Dispose();
    }

    private Uri Put(string path, byte[] content)
    {
        _server.Map(path, TestResponse.File(content));
        return _server.UrlFor(path);
    }

    private ModelStore Store(DownloadUrlPolicy? policy = null, params string[] bundled) =>
        new(_temp.Combine("modelos"), new FileDownloader(_http) { RetryDelay = TimeSpan.Zero }, policy ?? DownloadUrlPolicy.WithLoopback(), bundled);

    [Fact]
    public async Task A_model_is_stored_once_by_its_hash()
    {
        byte[] data = TestPayloads.Deterministic(200_000, seed: 2);
        string sha = TestPayloads.Sha256Hex(data);
        var model = new ModelSpec("modnet", Put("modnet.onnx", data), sha, data.Length);

        string path = await Store().EnsureAsync(model, null, default);
        Assert.Equal(_temp.Combine("modelos", sha), path);
        Assert.Equal(data, File.ReadAllBytes(path));
        await Store().EnsureAsync(model, null, default);
        Assert.Single(_server.Requests);
    }

    [Fact]
    public async Task A_bundled_model_is_used_without_downloading()
    {
        byte[] data = [1, 2, 3, 4];
        string sha = TestPayloads.Sha256Hex(data);
        string bundled = _temp.Combine("junto-al-exe");
        Directory.CreateDirectory(bundled);
        File.WriteAllBytes(Path.Combine(bundled, sha), data);
        string path = await Store(null, bundled).EnsureAsync(new ModelSpec("m", _server.UrlFor("m"), sha, data.Length), null, default);
        Assert.Equal(Path.Combine(bundled, sha), path);
        Assert.Empty(_server.Requests);
    }

    [Fact]
    public async Task Unpinned_hugging_face_models_are_refused()
    {
        var model = new ModelSpec("m", new Uri("https://huggingface.co/org/m/resolve/main/m.onnx"), new string('a', 64), 10);
        await Assert.ThrowsAsync<PackageDownloadException>(() => Store(DownloadUrlPolicy.Default).EnsureAsync(model, null, default));
    }

    [Fact]
    public async Task A_model_with_a_wrong_hash_is_deleted()
    {
        var model = new ModelSpec("m", Put("m", [9, 9, 9]), new string('b', 64), 3);
        await Assert.ThrowsAsync<DownloadVerificationException>(() => Store().EnsureAsync(model, null, default));
        Assert.Empty(Directory.EnumerateFiles(_temp.Combine("modelos")));
    }

    [Fact]
    public async Task A_model_is_placed_in_the_module_folder_as_a_hard_link_and_pruned_when_unused()
    {
        byte[] data = TestPayloads.Deterministic(100_000, seed: 8);
        string sha = TestPayloads.Sha256Hex(data);
        var model = new ModuleModel("voz", "modelos/voz/kokoro.onnx", data.Length, sha, Put("voz", data), "Apache-2.0");
        string module = _temp.Combine("modulos", "modulo-voz");
        ModelStore store = Store();

        string placed = await store.PlaceAsync(model, module, null, default);

        Assert.Equal(Path.Combine(module, "modelos", "voz", "kokoro.onnx"), placed);
        Assert.Equal(data, File.ReadAllBytes(placed));
        Assert.True(model.IsPresentIn(module));
        await store.PlaceAsync(model, module, null, default);
        Assert.Single(_server.Requests);

        Assert.Equal(0, store.Prune([sha]));
        Assert.Equal(data.Length, store.Prune([]));
        Assert.False(File.Exists(Path.Combine(store.Folder, sha)));
        Assert.Equal(data, File.ReadAllBytes(placed));
    }

    [Fact]
    public void Module_models_are_read_from_the_manifest_and_bad_ones_skipped()
    {
        string manifest = _temp.Combine("paquete.json");
        string sha = new('a', 64);
        File.WriteAllText(manifest, $$"""
            { "id": "m", "module": { "entry": "m.exe", "models": [
              { "id": "bueno", "file": "modelos/a.onnx", "size": 10, "sha256": "{{sha}}", "license": "MIT", "url": "https://huggingface.co/o/r/resolve/{{new string('b', 40)}}/a.onnx" },
              { "id": "fuera", "file": "../a.onnx", "size": 10, "sha256": "{{sha}}", "url": "https://huggingface.co/x" },
              { "id": "sin-hash", "file": "modelos/b.onnx", "size": 10, "url": "https://huggingface.co/x" },
              { "id": "repetido", "file": "MODELOS/A.onnx", "size": 10, "sha256": "{{sha}}", "url": "https://huggingface.co/x" } ] } }
            """);
        var problems = new List<string>();

        IReadOnlyList<ModuleModel> models = ModuleModels.Read(manifest, problems);

        ModuleModel only = Assert.Single(models);
        Assert.Equal(("bueno", "modelos/a.onnx", 10L, "MIT"), (only.Id, only.File, only.Size, only.License));
        Assert.Equal(3, problems.Count);
        Assert.Empty(ModuleModels.Read(_temp.Combine("no-existe.json"), problems));
    }
}
