using System.Text;
using Rodaje.Packages.Download;
using Rodaje.TestServer;

namespace Rodaje.Packages.Tests.Download;

/// <summary>
/// La fecha <c>generated</c> del índice contra la última vista (<c>descargas\indice-visto.txt</c>): un índice más viejo
/// no se acepta aunque esté bien firmado, también si la fecha viene con otro huso horario o falta. Por un servidor HTTP
/// propio en 127.0.0.1, como <see cref="GitHubReleaseDownloaderTests"/>.
/// </summary>
public sealed class IndexRollbackTests : IAsyncLifetime, IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly TestHttpServer _server = TestHttpServer.Start();
    private readonly HttpClient _http = new();
    private readonly TestIndexSigner _signer = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        Assert.Empty(_server.Faults);
    }

    public void Dispose()
    {
        _http.Dispose();
        _signer.Dispose();
        _temp.Dispose();
    }

    private string Downloads => _temp.Combine("descargas");

    private GitHubReleaseDownloader Downloader() => new(new GitHubReleaseOptions
    {
        IndexUrl = _server.UrlFor("indice.json"),
        PublicKey = _signer.PublicKeyBase64,
        DownloadFolder = Downloads,
        AppVersion = PackageVersion.Parse("1.0.0"),
        Policy = DownloadUrlPolicy.WithLoopback("Dathinel"),
    }, _http);

    /// <summary>Publica un índice vacío con <paramref name="generated"/> (el JSON tal cual, o sin la propiedad si es <c>null</c>).</summary>
    private void Publish(string? generated)
    {
        byte[] index = Encoding.UTF8.GetBytes(generated is null
            ? """{ "formatVersion": 1, "entries": [] }"""
            : $$"""{ "formatVersion": 1, "generated": {{generated}}, "entries": [] }""");
        _server.Map("indice.json", TestResponse.File(index));
        _server.Map("indice.json.sig", TestResponse.File(_signer.SignFile(index)));
    }

    private async Task<SignedIndex> ReadAsync()
    {
        using GitHubReleaseDownloader downloader = Downloader();
        return await downloader.GetIndexAsync(refresh: true);
    }

    [Theory]
    [InlineData("\"2026-10-12T13:59:59+02:00\"")]
    [InlineData("\"2026-10-12T11:59:59Z\"")]
    [InlineData("\"2026-10-12T07:59:59-04:00\"")]
    [InlineData("\"2026-10-12T11:59:59.9999999Z\"")]
    [InlineData("\"ayer\"")]
    [InlineData("12")]
    [InlineData(null)]
    public async Task Un_indice_anterior_al_visto_se_rechaza_aunque_venga_en_otro_huso(string? older)
    {
        Publish("\"2026-10-12T12:00:00Z\"");
        await ReadAsync();

        Publish(older);
        await Assert.ThrowsAsync<DownloadVerificationException>(ReadAsync);

        // El rechazo no borra lo visto: el índice bueno sigue valiendo y el viejo sigue sin valer.
        Publish("\"2026-10-12T12:00:00Z\"");
        await ReadAsync();
        Publish(older);
        await Assert.ThrowsAsync<DownloadVerificationException>(ReadAsync);
    }

    [Theory]
    [InlineData("\"2026-10-12T12:00:00Z\"")]
    [InlineData("\"2026-10-12T14:00:00+02:00\"")]
    [InlineData("\"2026-10-12T12:00:00\"")]
    [InlineData("\"2026-10-12T12:00:00.0000001Z\"")]
    [InlineData("\"2027-01-01T00:00:00Z\"")]
    public async Task El_mismo_instante_o_uno_posterior_se_acepta(string newer)
    {
        Publish("\"2026-10-12T12:00:00Z\"");
        await ReadAsync();
        Publish(newer);
        SignedIndex index = await ReadAsync();
        Assert.True(index.Generated >= new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task Lo_visto_avanza_y_no_vuelve_atras()
    {
        string[] sequence = ["\"2026-10-01T00:00:00Z\"", "\"2026-10-05T00:00:00Z\"", "\"2026-10-10T00:00:00+05:00\"", "\"2026-10-12T00:00:00Z\""];
        foreach (string generated in sequence)
        {
            Publish(generated);
            await ReadAsync();
        }

        foreach (string generated in sequence[..^1])
        {
            Publish(generated);
            await Assert.ThrowsAsync<DownloadVerificationException>(ReadAsync);
        }

        Publish(sequence[^1]);
        await ReadAsync();
    }

    [Fact]
    public async Task Una_firma_mala_no_cambia_lo_visto()
    {
        Publish("\"2026-10-12T12:00:00Z\"");
        await ReadAsync();

        // Un índice «del futuro» con la firma de otros bytes no se acepta ni se recuerda.
        byte[] future = Encoding.UTF8.GetBytes("""{ "formatVersion": 1, "generated": "2099-01-01T00:00:00Z", "entries": [] }""");
        _server.Map("indice.json", TestResponse.File(future));
        _server.Map("indice.json.sig", TestResponse.File(Encoding.ASCII.GetBytes(_signer.SignOtherData(future))));
        await Assert.ThrowsAsync<DownloadVerificationException>(ReadAsync);

        Publish("\"2026-10-12T12:00:01Z\"");
        await ReadAsync();
    }
}
