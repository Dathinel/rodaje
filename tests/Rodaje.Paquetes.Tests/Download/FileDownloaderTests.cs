using Rodaje.Paquetes.Download;
using Rodaje.TestServer;

namespace Rodaje.Paquetes.Tests.Download;

public sealed class FileDownloaderTests : IAsyncLifetime, IDisposable
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

    private static byte[] Content(int size, int seed = 1) => TestPayloads.Deterministic(size, seed);

    private static DownloadTarget Target(Uri url, byte[] data) => new(url, TestPayloads.Sha256Hex(data), data.Length);

    private Uri Put(string path, byte[] content)
    {
        _server.Map(path, TestResponse.File(content));
        return _server.UrlFor(path);
    }

    private IEnumerable<string> Ranges() => _server.Requests.Select(r => r.Range ?? "");

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
        await Downloader().DownloadAsync(Target(Put("a", data), data), destination, new SyncProgress(reports.Add), default);

        Assert.Equal(data, File.ReadAllBytes(destination));
        Assert.False(File.Exists(destination + FileDownloader.PartialSuffix));
        Assert.Equal(new DownloadProgress(data.Length, data.Length), reports[^1]);
    }

    [Fact]
    public async Task Every_bad_hash_is_rejected_and_deleted()
    {
        // 200 archivos cuyo contenido no es el publicado: un byte cambiado en cualquier lugar, el primero, el último.
        byte[] data = Content(20_000, seed: 7);
        DownloadTarget good = Target(Put("bueno", data), data);
        var random = new Random(3);
        int rejected = 0;
        for (int i = 0; i < 200; i++)
        {
            int position = i switch { 0 => 0, 1 => data.Length - 1, _ => random.Next(data.Length) };
            Uri url = Put($"malo{i}", TestPayloads.Corrupt(data, position));
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
        DownloadTarget target = Target(Put("ok", data), data);
        Uri longer = Put("largo", [.. data, 1, 2, 3]);
        string a = _temp.Combine("largo.paquete");
        await Assert.ThrowsAsync<DownloadVerificationException>(() => Downloader().DownloadAsync(target with { Url = longer }, a, null, default));
        AssertNothingLeft(a);

        // Uno más corto que nunca llega al tamaño: se reintenta (el servidor contesta 416 al rango) y al final se
        // informa, sin dejar el destino.
        Uri shorter = Put("corto", data[..5000]);
        string b = _temp.Combine("corto.paquete");
        await Assert.ThrowsAsync<PackageDownloadException>(() => new FileDownloader(_http) { RetryDelay = TimeSpan.Zero, MaxAttempts = 2 }
            .DownloadAsync(target with { Url = shorter }, b, null, default));
        Assert.False(File.Exists(b));
        Assert.Equal([200, 416], _server.Requests.Where(r => r.Path == "/corto").Select(r => r.StatusCode));
    }

    [Fact]
    public async Task Resumes_with_range_after_the_network_drops()
    {
        // Dos cortes con CutSignal: el servidor corta solo cuando el descargador ya guardó lo mandado, así el conteo
        // es exacto también en Windows (HTTP.sys descarta lo no leído al cortar).
        byte[] data = Content(1_000_000);
        var first = new CutSignal();
        var second = new CutSignal();
        _server.Map("grande",
            TestResponse.File(data, "\"v1\"") with { CutAfterBytes = 400_000, CutSignal = first },
            TestResponse.File(data, "\"v1\"") with { CutAfterBytes = 300_000, CutSignal = second },
            TestResponse.File(data, "\"v1\""));
        var progress = new SyncProgress(p =>
        {
            if (p.Received >= 400_000)
            {
                first.Release();
            }

            if (p.Received >= 700_000)
            {
                second.Release();
            }
        });
        string destination = _temp.Combine("grande.paquete");
        FileDownloader downloader = Downloader();
        await downloader.DownloadAsync(Target(_server.UrlFor("grande"), data), destination, progress, default);

        Assert.Equal(data, File.ReadAllBytes(destination));
        Assert.Equal(["", "bytes=400000-", "bytes=700000-"], Ranges());
        Assert.Equal([200, 206, 206], _server.Requests.Select(r => r.StatusCode));
        Assert.Equal(data.Length, downloader.BytesReceived);
    }

    [Fact]
    public async Task Resumes_after_an_immediate_cut()
    {
        // Sin señal el corte es inmediato: en Windows pueden llegar menos bytes de los mandados, así que solo se
        // exige que reanude desde lo que guardó y que el archivo final sea el correcto.
        byte[] data = Content(500_000, seed: 4);
        _server.Map("x", TestResponse.File(data) with { CutAfterBytes = 200_000 }, TestResponse.File(data));
        string destination = _temp.Combine("x.paquete");
        FileDownloader downloader = Downloader();
        await downloader.DownloadAsync(Target(_server.UrlFor("x"), data), destination, null, default);

        Assert.Equal(data, File.ReadAllBytes(destination));
        Assert.Equal(2, _server.RequestCount("x"));
        string resumed = _server.Requests[1].Range ?? "";
        long offset = resumed.Length == 0 ? 0 : long.Parse(resumed["bytes=".Length..^1], System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(offset, 0, 200_000);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(200_000, offset);
        }

        // Cada byte se pidió una sola vez: lo guardado antes del corte más lo que faltaba.
        Assert.Equal(data.Length, downloader.BytesReceived);
    }

    [Fact]
    public async Task Resumes_after_cancelling_with_a_new_downloader()
    {
        byte[] data = Content(2_000_000);
        Uri url = Put("grande", data);
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
        Assert.Equal($"bytes={kept}-", _server.Requests[^1].Range);
        Assert.Equal(206, _server.Requests[^1].StatusCode);
    }

    [Fact]
    public async Task A_damaged_partial_is_downloaded_again_from_zero()
    {
        // Un apagón puede dejar basura al final del parcial: el hash no coincide, se borra y se baja entero una vez.
        byte[] data = Content(100_000);
        string destination = _temp.Combine("x.paquete");
        File.WriteAllBytes(destination + FileDownloader.PartialSuffix, new byte[40_000]);
        await Downloader().DownloadAsync(Target(Put("x", data), data), destination, null, default);
        Assert.Equal(data, File.ReadAllBytes(destination));
        Assert.Equal(["bytes=40000-", ""], Ranges());
    }

    [Fact]
    public async Task A_file_that_changed_between_cut_and_resume_is_caught()
    {
        // El primer intento baja la mitad de la versión 1 y se corta; luego el servidor ya sirve otro contenido (con
        // otro ETag). Hoy el descargador no manda If-Range, así que une las dos mitades; con If-Range el servidor
        // mandaría el archivo nuevo entero. En los dos casos el hash lo rechaza, se reintenta una vez desde cero y
        // no queda nada.
        byte[] v1 = Content(200_000, seed: 11);
        byte[] v2 = TestPayloads.Corrupt(v1, 150_000);
        var cut = new CutSignal();
        _server.Map("x", TestResponse.File(v1, "\"v1\"") with { CutAfterBytes = 100_000, CutSignal = cut }, TestResponse.File(v2, "\"v2\""));
        var progress = new SyncProgress(p =>
        {
            if (p.Received >= 100_000)
            {
                cut.Release();
            }
        });
        string destination = _temp.Combine("x.paquete");

        await Assert.ThrowsAsync<DownloadVerificationException>(() => Downloader().DownloadAsync(Target(_server.UrlFor("x"), v1), destination, progress, default));
        AssertNothingLeft(destination);
        Assert.Equal(3, _server.RequestCount("x"));
        Assert.Equal("", _server.Requests[^1].Range ?? "");
    }

    [Fact]
    public async Task A_server_without_range_starts_over()
    {
        byte[] data = Content(100_000);
        _server.Map("x", TestResponse.File(data) with { AcceptRanges = false });
        string destination = _temp.Combine("x.paquete");
        File.WriteAllBytes(destination + FileDownloader.PartialSuffix, data[..30_000]);
        await Downloader().DownloadAsync(Target(_server.UrlFor("x"), data), destination, null, default);
        Assert.Equal(data, File.ReadAllBytes(destination));
        Assert.Equal(["bytes=30000-"], Ranges());
        Assert.Equal(200, _server.Requests[0].StatusCode);
    }

    [Fact]
    public async Task A_complete_partial_or_destination_is_not_downloaded_again()
    {
        byte[] data = Content(50_000);
        Uri url = Put("x", data);
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
        // Como GitHub: la dirección de la versión da un 503 y luego redirige a otro servidor, que da un 500 antes del archivo.
        await using var storage = TestHttpServer.Start();
        byte[] data = Content(50_000);
        storage.Map("cdn/x", TestResponse.Status(500, "falla"), TestResponse.File(data));
        _server.Map("release/x", TestResponse.Status(503), TestResponse.Redirect(storage.UrlFor("cdn/x").ToString()));
        string destination = _temp.Combine("x.paquete");
        await Downloader().DownloadAsync(Target(_server.UrlFor("release/x"), data), destination, null, default);

        Assert.Equal(data, File.ReadAllBytes(destination));
        Assert.Equal([503, 302, 302], _server.Requests.Select(r => r.StatusCode));
        Assert.Equal([500, 200], storage.Requests.Select(r => r.StatusCode));
        Assert.Empty(storage.Faults);
    }

    [Fact]
    public async Task A_redirect_loop_fails_without_leaving_anything()
    {
        byte[] data = Content(1000);
        _server.Map("a", TestResponse.Redirect(_server.UrlFor("b").ToString()));
        _server.Map("b", TestResponse.Redirect(_server.UrlFor("a").ToString()));
        string destination = _temp.Combine("x.paquete");

        await Assert.ThrowsAsync<PackageDownloadException>(() => Downloader().DownloadAsync(Target(_server.UrlFor("a"), data), destination, null, default));
        AssertNothingLeft(destination);
    }

    [Fact]
    public async Task A_missing_file_fails_without_leaving_anything()
    {
        byte[] data = Content(1000);
        string destination = _temp.Combine("x.paquete");
        PackageDownloadException ex = await Assert.ThrowsAsync<PackageDownloadException>(
            () => Downloader().DownloadAsync(Target(_server.UrlFor("no-existe"), data), destination, null, default));
        Assert.Contains("404", ex.Message, StringComparison.Ordinal);
        AssertNothingLeft(destination);
    }

    private sealed class SyncProgress(Action<DownloadProgress> report) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => report(value);
    }
}
