using Rodaje.Packages.Download;

namespace Rodaje.Packages.Tests.Download;

/// <summary>
/// <see cref="FileDownloader.MaxBytes"/>: una entrada del índice que declare más del tope se rechaza antes de
/// pedir un solo byte (un índice o espejo comprometido no puede inflar la descarga).
/// </summary>
public sealed class FileDownloaderMaxBytesTests
{
    private const string Sha = "0000000000000000000000000000000000000000000000000000000000000000";

    [Fact]
    public async Task UnaDescargaQueDeclaraMasDelTope_NoSePide()
    {
        using var http = new HttpClient(new Stub());
        var cut = new FileDownloader(http, maxBytes: 100);

        var target = new DownloadTarget(new Uri("https://cdn.example.com/x"), Sha, 101);

        await Assert.ThrowsAsync<DownloadVerificationException>(() =>
            cut.DownloadAsync(target, Path.Combine(Path.GetTempPath(), "nunca.paquete"), null, CancellationToken.None));
    }

    [Fact]
    public void UnTopeDeCeroOMenos_NoSeAdmite()
    {
        using var http = new HttpClient(new Stub());

        Assert.Throws<ArgumentOutOfRangeException>(() => new FileDownloader(http, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FileDownloader(http, -1));
    }

    /// <summary>Fallaría si se llamara: la prueba exige que la petición nunca salga.</summary>
    private sealed class Stub : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No debió hacerse la petición.");
    }
}
