using System.Text;
using Rodaje.Packages.Download;
using Rodaje.TestServer;

namespace Rodaje.Packages.Tests.Download;

/// <summary>
/// Familia 4: catálogo e índice firmado, lo que no cubren <see cref="IndexRollbackTests"/> ni <see cref="SignedIndexHostileTests"/>:
/// un índice con fecha futura, el mismo id en varias versiones y un catálogo con entradas repetidas o mal escritas.
/// </summary>
public sealed class CatalogAndIndexHardenedTests : IAsyncLifetime, IDisposable
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

    private GitHubReleaseDownloader Downloader(DownloadUrlPolicy? policy = null) => new(new GitHubReleaseOptions
    {
        IndexUrl = _server.UrlFor("indice.json"),
        PublicKey = _signer.PublicKeyBase64,
        DownloadFolder = Downloads,
        AppVersion = PackageVersion.Parse("1.0.0"),
        Policy = policy ?? DownloadUrlPolicy.WithLoopback("Dathinel"),
    }, _http);

    private void Publish(byte[] indexBytes, byte[]? signatureBytes = null)
    {
        _server.Map("indice.json", TestResponse.File(indexBytes));
        _server.Map("indice.json.sig", TestResponse.File(signatureBytes ?? _signer.SignFile(indexBytes)));
    }

    [Fact]
    public async Task Indice_ConFechaFutura_AceptadoYRecuerdaAvance()
    {
        byte[] futureIndex = Encoding.UTF8.GetBytes("""{ "formatVersion": 1, "generated": "2030-01-01T00:00:00Z", "entries": [] }""");
        Publish(futureIndex);

        using (GitHubReleaseDownloader downloader = Downloader())
        {
            SignedIndex index = await downloader.GetIndexAsync(refresh: true);
            Assert.Equal(2030, index.Generated.Year);
        }

        // Una vez aceptado el timestamp futuro, cualquier timestamp anterior a 2030 es rechazado
        byte[] presentIndex = Encoding.UTF8.GetBytes("""{ "formatVersion": 1, "generated": "2026-11-11T00:00:00Z", "entries": [] }""");
        Publish(presentIndex);

        using (GitHubReleaseDownloader downloader = Downloader())
        {
            var ex = await Assert.ThrowsAsync<DownloadVerificationException>(() => downloader.GetIndexAsync(refresh: true));
            Assert.Contains("más viejo", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Indice_MismoIdConDistintasVersiones_AmbasAceptadas()
    {
        string sha = new('a', 64);
        byte[] json = Encoding.UTF8.GetBytes($$"""
        {
          "formatVersion": 1,
          "generated": "2026-11-01T00:00:00Z",
          "entries": [
            {
              "id": "pkg-versiones",
              "version": "1.0.0",
              "kind": "content",
              "url": "https://github.com/Dathinel/repo/releases/download/v1.0/v1.paquete",
              "sha256": "{{sha}}",
              "size": 100
            },
            {
              "id": "pkg-versiones",
              "version": "1.2.0",
              "kind": "content",
              "url": "https://github.com/Dathinel/repo/releases/download/v1.2/v2.paquete",
              "sha256": "{{sha}}",
              "size": 200
            }
          ]
        }
        """);

        byte[] sig = _signer.SignFile(json);
        SignedIndex index = SignedIndex.Load(json, sig, IndexSignature.TryImportPublicKey(_signer.PublicKeyBase64)!, DownloadUrlPolicy.Default);

        Assert.Equal(2, index.Entries.Count);
        IndexEntry? latest = index.Latest("pkg-versiones", PackageVersion.Parse("1.0.0"));
        Assert.NotNull(latest);
        Assert.Equal(PackageVersion.Parse("1.2.0"), latest.Version);
    }

    [Fact]
    public void Catalogo_ElMismoIdDosVeces_SegundaOmitida()
    {
        string json = """
        {
          "formatVersion": 1,
          "apps": [],
          "entries": [
            {
              "id": "duplicado",
              "name": "Primero",
              "repository": "https://github.com/Dathinel/duplicado"
            },
            {
              "id": "duplicado",
              "name": "Segundo",
              "repository": "https://github.com/Dathinel/duplicado"
            }
          ]
        }
        """;

        var problems = new List<string>();
        PackageCatalog catalog = PackageCatalog.Parse(json, problems);

        Assert.Single(catalog.Entries);
        Assert.Equal("Primero", catalog.Entries[0].Name);
        Assert.Contains(problems, p => p.Contains("duplicado") && p.Contains("repetida"));
    }

    [Fact]
    public void Catalogo_EntradaConUrlNoHttps_Omitida()
    {
        string json = """
        {
          "formatVersion": 1,
          "apps": [],
          "entries": [
            {
              "id": "repo-http",
              "name": "Repo HTTP",
              "repository": "http://github.com/Dathinel/repo"
            }
          ]
        }
        """;

        var problems = new List<string>();
        PackageCatalog catalog = PackageCatalog.Parse(json, problems);

        Assert.Empty(catalog.Entries);
        Assert.Contains(problems, p => p.Contains("repo-http") && p.Contains("incompleta o mal escrita"));
    }

    [Fact]
    public void Catalogo_EntradaConRepositorioInvalido_Omitida()
    {
        string json = """
        {
          "formatVersion": 1,
          "apps": [],
          "entries": [
            {
              "id": "repo-bad",
              "name": "Repo Malo",
              "repository": "esto-no-es-una-uri"
            }
          ]
        }
        """;

        var problems = new List<string>();
        PackageCatalog catalog = PackageCatalog.Parse(json, problems);

        Assert.Empty(catalog.Entries);
        Assert.Contains(problems, p => p.Contains("repo-bad"));
    }

    [Fact]
    public void Catalogo_FormatVersionSuperior_LanzaFormatException()
    {
        string json = """
        {
          "formatVersion": 99,
          "apps": [],
          "entries": []
        }
        """;

        var ex = Assert.Throws<FormatException>(() => PackageCatalog.Parse(json));
        Assert.Contains("usa el formato 99", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Catalogo_AppsConIdInvalido_Omitidas()
    {
        string json = """
        {
          "formatVersion": 1,
          "apps": [
            { "id": "valida", "name": "Valida" },
            { "id": "INVALIDA-MAYUS", "name": "Invalida" }
          ],
          "entries": []
        }
        """;

        var problems = new List<string>();
        PackageCatalog catalog = PackageCatalog.Parse(json, problems);

        Assert.Single(catalog.Apps);
        Assert.Equal("valida", catalog.Apps[0].Id);
        Assert.Contains(problems, p => p.Contains("no tiene id o nombre válidos"));
    }
}
