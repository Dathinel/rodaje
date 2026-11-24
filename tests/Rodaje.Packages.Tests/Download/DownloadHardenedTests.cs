using Rodaje.Packages.Download;
using Rodaje.TestServer;

namespace Rodaje.Packages.Tests.Download;

/// <summary>
/// Familia 5: descargas con Rodaje.TestServer que <see cref="FileDownloaderTests"/> no cubre: dos descargas a la vez al
/// mismo destino, un 416 con un parcial más grande que lo publicado y un destino ya presente con otro contenido.
/// </summary>
public sealed class DownloadHardenedTests : IAsyncLifetime, IDisposable
{
    private readonly TempFolder _temp = new();
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
        _temp.Dispose();
    }

    private FileDownloader Downloader() => new(_http, SignedIndex.MaxEntrySize) { RetryDelay = TimeSpan.FromMilliseconds(5) };

    private static DownloadTarget Target(Uri url, byte[] data) => new(url, TestPayloads.Sha256Hex(data), data.Length);

    [Fact]
    public async Task Descarga_DosDescargasDelMismoArchivoALaVez_UnaTerminaYElDestinoQuedaIntacto()
    {
        byte[] data = TestPayloads.Deterministic(500_000, seed: 8);
        _server.Map("concurrente", TestResponse.File(data));
        string destination = _temp.Combine("concurrente.paquete");

        Task[] tasks =
        [
            Task.Run(() => Downloader().DownloadAsync(Target(_server.UrlFor("concurrente"), data), destination, null, default)),
            Task.Run(() => Downloader().DownloadAsync(Target(_server.UrlFor("concurrente"), data), destination, null, default)),
        ];
        await Task.WhenAll(tasks.Select(t => t.ContinueWith(_ => { }, TaskScheduler.Default)));

        // La que pierde solo puede fallar porque la otra tiene abierto el parcial; nunca por verificación ni dejando basura.
        Assert.Contains(tasks, t => t.IsCompletedSuccessfully);
        Assert.All(tasks.Where(t => t.IsFaulted), t => Assert.IsAssignableFrom<IOException>(t.Exception!.InnerException));
        Assert.Equal(data, File.ReadAllBytes(destination));
        Assert.False(File.Exists(destination + FileDownloader.PartialSuffix));
    }

    [Fact]
    public async Task Descarga_ParcialMayorQueLoPublicadoYUn416_EmpiezaDeCeroYCompleta()
    {
        byte[] data = TestPayloads.Deterministic(40_000, seed: 10);
        _server.Map("rango-malo", TestResponse.Status(416, "Range Not Satisfiable"), TestResponse.File(data));
        string destination = _temp.Combine("rango-malo.paquete");
        File.WriteAllBytes(destination + FileDownloader.PartialSuffix, new byte[50_000]);

        await Downloader().DownloadAsync(Target(_server.UrlFor("rango-malo"), data), destination, null, default);

        Assert.Equal(data, File.ReadAllBytes(destination));
        Assert.False(File.Exists(destination + FileDownloader.PartialSuffix));
        // El parcial sobrante se descarta sin pedir un tramo imposible: ninguna petición lleva Range.
        Assert.Equal([416, 200], _server.Requests.Select(r => r.StatusCode));
        Assert.All(_server.Requests, r => Assert.Null(r.Range));
    }

    [Fact]
    public async Task Descarga_ArchivoPresenteConHashDiferente_SeBorraYDescargaElNuevo()
    {
        byte[] data = TestPayloads.Deterministic(15_000, seed: 12);
        _server.Map("hash-cambio", TestResponse.File(data));
        string destination = _temp.Combine("hash-cambio.paquete");
        File.WriteAllBytes(destination, new byte[15_000]);

        await Downloader().DownloadAsync(Target(_server.UrlFor("hash-cambio"), data), destination, null, default);

        Assert.Equal(data, File.ReadAllBytes(destination));
        Assert.Equal(1, _server.RequestCount("hash-cambio"));
    }
}
