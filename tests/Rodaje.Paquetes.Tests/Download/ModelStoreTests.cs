using Rodaje.Paquetes.Download;
using Rodaje.TestServer;

namespace Rodaje.Paquetes.Tests.Download;

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
}
