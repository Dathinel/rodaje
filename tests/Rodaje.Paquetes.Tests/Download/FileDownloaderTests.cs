using System.Security.Cryptography;
using Rodaje.Paquetes.Download;

namespace Rodaje.Paquetes.Tests.Download;

public sealed class FileDownloaderTests : IDisposable
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

    private static byte[] Content(int size, int seed = 1)
    {
        byte[] data = new byte[size];
        new Random(seed).NextBytes(data);
        return data;
    }

    private static DownloadTarget Target(Uri url, byte[] data) => new(url, Convert.ToHexStringLower(SHA256.HashData(data)), data.Length);

    private FileDownloader Downloader() => new(_http) { RetryDelay = TimeSpan.FromMilliseconds(10) };

    private static void AssertNothingLeft(string destination)
    {
        Assert.False(File.Exists(destination));
        Assert.False(File.Exists(destination + FileDownloader.PartialSuffix));
    }

    [Fact]
    public async Task Downloads_and_verifies_a_file()
    {
        byte[] data = Content(300_000);
        string destination = _temp.Combine("a.paquete");
        var reports = new List<DownloadProgress>();
        await Downloader().DownloadAsync(Target(_server.Put("a", data), data), destination, new Progress<DownloadProgress>(reports.Add), default);

        Assert.Equal(data, File.ReadAllBytes(destination));
        Assert.False(File.Exists(destination + FileDownloader.PartialSuffix));
    }

    [Fact]
    public async Task Every_bad_hash_is_rejected_and_deleted()
    {
        // 200 archivos cuyo contenido no es el publicado: un byte cambiado en cualquier lugar, el primero, el último.
        byte[] data = Content(20_000, seed: 7);
        DownloadTarget good = Target(_server.Put("bueno", data), data);
        var random = new Random(3);
        int rejected = 0;
        for (int i = 0; i < 200; i++)
        {
            byte[] bad = (byte[])data.Clone();
            int position = i switch { 0 => 0, 1 => bad.Length - 1, _ => random.Next(bad.Length) };
            bad[position] ^= (byte)(1 + random.Next(255));
            Uri url = _server.Put($"malo{i}", bad);
            string destination = _temp.Combine($"malo{i}.paquete");
            await Assert.ThrowsAsync<DownloadVerificationException>(() => Downloader().DownloadAsync(good with { Url = url }, destination, null, default));
            AssertNothingLeft(destination);
            rejected++;
        }

        Assert.Equal(200, rejected);
    }

    [Fact]
    public async Task Larger_or_shorter_files_are_rejected_and_deleted()
    {
        byte[] data = Content(10_000);
        DownloadTarget target = Target(_server.Put("ok", data), data);
        Uri longer = _server.Put("largo", [.. data, 1, 2, 3]);
        string a = _temp.Combine("largo.paquete");
        await Assert.ThrowsAsync<DownloadVerificationException>(() => Downloader().DownloadAsync(target with { Url = longer }, a, null, default));
        AssertNothingLeft(a);

        // Uno más corto que nunca llega al tamaño: se reintenta y al final se informa, sin dejar el destino.
        Uri shorter = _server.Put("corto", data[..5000]);
        string b = _temp.Combine("corto.paquete");
        await Assert.ThrowsAsync<PackageDownloadException>(() => new FileDownloader(_http) { RetryDelay = TimeSpan.Zero, MaxAttempts = 2 }
            .DownloadAsync(target with { Url = shorter }, b, null, default));
        Assert.False(File.Exists(b));
    }

    [Fact]
    public async Task Resumes_with_range_after_the_network_drops()
    {
        byte[] data = Content(1_000_000);
        Uri url = _server.Put("grande", data);
        _server.CutAfter.Enqueue(400_000);
        _server.CutAfter.Enqueue(300_000);
        string destination = _temp.Combine("grande.paquete");
        FileDownloader downloader = Downloader();
        await downloader.DownloadAsync(Target(url, data), destination, null, default);

        Assert.Equal(data, File.ReadAllBytes(destination));
        Assert.Equal(["", "bytes=400000-", "bytes=700000-"], _server.Requests.Select(r => r.Range ?? ""));
        Assert.Equal(data.Length, downloader.BytesReceived);
    }

    [Fact]
    public async Task Resumes_after_cancelling_with_a_new_downloader()
    {
        byte[] data = Content(2_000_000);
        Uri url = _server.Put("grande", data);
        string destination = _temp.Combine("grande.paquete");
        using var cancel = new CancellationTokenSource();
        var progress = new SyncProgress(p =>
        {
            if (p.Received > 500_000)
            {
                cancel.Cancel();
            }
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Downloader().DownloadAsync(Target(url, data), destination, progress, cancel.Token));
        long kept = new FileInfo(destination + FileDownloader.PartialSuffix).Length;
        Assert.InRange(kept, 500_001, data.Length - 1);
        Assert.False(File.Exists(destination));

        FileDownloader second = Downloader();
        await second.DownloadAsync(Target(url, data), destination, null, default);
        Assert.Equal(data, File.ReadAllBytes(destination));
        Assert.Equal(data.Length - kept, second.BytesReceived);
        Assert.Equal($"bytes={kept}-", _server.Requests.Last().Range);
    }

    [Fact]
    public async Task A_damaged_partial_is_downloaded_again_from_zero()
    {
        // Un apagón puede dejar basura al final del parcial: el hash no coincide, se borra y se baja entero una vez.
        byte[] data = Content(100_000);
        string destination = _temp.Combine("x.paquete");
        File.WriteAllBytes(destination + FileDownloader.PartialSuffix, new byte[40_000]);
        await Downloader().DownloadAsync(Target(_server.Put("x", data), data), destination, null, default);
        Assert.Equal(data, File.ReadAllBytes(destination));
        Assert.Equal(["bytes=40000-", ""], _server.Requests.Select(r => r.Range ?? ""));
    }

    [Fact]
    public async Task A_server_without_range_starts_over()
    {
        byte[] data = Content(100_000);
        _server.SupportsRange = false;
        string destination = _temp.Combine("x.paquete");
        File.WriteAllBytes(destination + FileDownloader.PartialSuffix, data[..30_000]);
        await Downloader().DownloadAsync(Target(_server.Put("x", data), data), destination, null, default);
        Assert.Equal(data, File.ReadAllBytes(destination));
    }

    [Fact]
    public async Task A_complete_partial_or_destination_is_not_downloaded_again()
    {
        byte[] data = Content(50_000);
        Uri url = _server.Put("x", data);
        string destination = _temp.Combine("x.paquete");
        File.WriteAllBytes(destination + FileDownloader.PartialSuffix, data);
        await Downloader().DownloadAsync(Target(url, data), destination, null, default);
        Assert.Empty(_server.Requests);
        await Downloader().DownloadAsync(Target(url, data), destination, null, default);
        Assert.Empty(_server.Requests);
        Assert.Equal(data, File.ReadAllBytes(destination));
    }

    [Fact]
    public async Task Follows_redirects_and_retries_server_errors()
    {
        byte[] data = Content(50_000);
        _server.Put("cdn/x", data);
        Uri url = _server.Redirect("release/x", "cdn/x");
        _server.FailWith.Enqueue(503);
        string destination = _temp.Combine("x.paquete");
        await Downloader().DownloadAsync(Target(url, data), destination, null, default);
        Assert.Equal(data, File.ReadAllBytes(destination));
    }

    [Fact]
    public async Task A_missing_file_fails_without_leaving_anything()
    {
        byte[] data = Content(1000);
        string destination = _temp.Combine("x.paquete");
        PackageDownloadException ex = await Assert.ThrowsAsync<PackageDownloadException>(
            () => Downloader().DownloadAsync(Target(new Uri(_server.BaseUrl, "no-existe"), data), destination, null, default));
        Assert.Contains("404", ex.Message, StringComparison.Ordinal);
        AssertNothingLeft(destination);
    }

    private sealed class SyncProgress(Action<DownloadProgress> report) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => report(value);
    }
}
