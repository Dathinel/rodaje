using System.Security.Cryptography;
using Rodaje.Paquetes.Download;

namespace Rodaje.Paquetes.Tests.Download;

public sealed class ModelStoreTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly LocalHttpServer _server = new();
    private readonly HttpClient _http = new();

    public void Dispose()
    {
        _http.Dispose();
        _server.Dispose();
        _temp.Dispose();
    }

    private ModelStore Store(DownloadUrlPolicy? policy = null, params string[] bundled) =>
        new(_temp.Combine("modelos"), new FileDownloader(_http) { RetryDelay = TimeSpan.Zero }, policy ?? DownloadUrlPolicy.WithLoopback(), bundled);

    [Fact]
    public async Task A_model_is_stored_once_by_its_hash()
    {
        byte[] data = new byte[200_000];
        new Random(2).NextBytes(data);
        string sha = Convert.ToHexStringLower(SHA256.HashData(data));
        var model = new ModelSpec("modnet", _server.Put("modnet.onnx", data), sha, data.Length);

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
        string sha = Convert.ToHexStringLower(SHA256.HashData(data));
        string bundled = _temp.Combine("junto-al-exe");
        Directory.CreateDirectory(bundled);
        File.WriteAllBytes(Path.Combine(bundled, sha), data);
        string path = await Store(null, bundled).EnsureAsync(new ModelSpec("m", new Uri(_server.BaseUrl, "m"), sha, data.Length), null, default);
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
        var model = new ModelSpec("m", _server.Put("m", [9, 9, 9]), new string('b', 64), 3);
        await Assert.ThrowsAsync<DownloadVerificationException>(() => Store().EnsureAsync(model, null, default));
        Assert.Empty(Directory.EnumerateFiles(_temp.Combine("modelos")));
    }
}
