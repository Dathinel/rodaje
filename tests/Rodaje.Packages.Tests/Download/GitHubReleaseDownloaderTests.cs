using System.Text;
using Rodaje.Packages.Download;
using Rodaje.TestServer;

namespace Rodaje.Packages.Tests.Download;

public sealed class GitHubReleaseDownloaderTests : IAsyncLifetime, IDisposable
{
    private readonly TempFolder _temp = new();

    /// <summary>Hace de github.com: el índice, su firma y las direcciones de las versiones, que redirigen a <see cref="_storage"/>.</summary>
    private readonly TestHttpServer _server = TestHttpServer.Start();

    /// <summary>Hace de objects.githubusercontent.com: los bytes de los paquetes.</summary>
    private readonly TestHttpServer _storage = TestHttpServer.Start();

    private readonly HttpClient _http = new();
    private readonly TestIndexSigner _signer = new();

    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>Cierra los servidores y comprueba que no tuvieron fallos propios (xunit 2 la llama antes de <see cref="Dispose"/>).</summary>
    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        await _storage.DisposeAsync();
        Assert.Empty(_server.Faults);
        Assert.Empty(_storage.Faults);
    }

    public void Dispose()
    {
        _http.Dispose();
        _signer.Dispose();
        _temp.Dispose();
    }

    private static readonly CatalogEntry Filtros = new("rodaje-transiciones", PackageKind.Content, "Transiciones", "", new Uri("https://github.com/Dathinel/rodaje-transiciones"), ["rodaje"], []);

    private string Downloads => _temp.Combine("descargas");

    private static string ReleasePath(string version) => $"Dathinel/rodaje-transiciones/releases/download/v{version}/rodaje-transiciones.paquete";

    private static string StoragePath(string version) => $"objetos/{version}";

    private GitHubReleaseOptions Options(string? publicKey = "", DownloadUrlPolicy? policy = null) => new()
    {
        IndexUrl = _server.UrlFor("indice.json"),
        PublicKey = publicKey == "" ? _signer.PublicKeyBase64 : publicKey,
        DownloadFolder = Downloads,
        AppVersion = PackageVersion.Parse("0.7.0"),
        Policy = policy ?? DownloadUrlPolicy.WithLoopback("Dathinel"),
    };

    private GitHubReleaseDownloader Downloader(string? publicKey = "") => new(Options(publicKey), _http)
    {
        Files = new FileDownloader(_http) { RetryDelay = TimeSpan.FromMilliseconds(10) },
    };

    /// <summary>
    /// Publica un paquete (la dirección de la versión redirige al almacenamiento, como en GitHub) y un índice que lo
    /// lista con el hash dado (por defecto, el del archivo), firmado con <paramref name="sign"/> (por defecto, bien).
    /// </summary>
    private (byte[] Package, string Sha) Publish(string version = "1.0.0", string? sha = null, string generated = "2026-10-11T12:00:00Z", Func<byte[], byte[]>? sign = null)
    {
        string path = Samples.ValidPackage(_temp.Combine($"p-{version}.paquete"), version: version);
        byte[] package = File.ReadAllBytes(path);
        string actual = TestPayloads.Sha256Hex(package);
        _storage.Map(StoragePath(version), TestResponse.File(package, $"\"{actual}\""));
        _server.Map(ReleasePath(version), TestResponse.Redirect(_storage.UrlFor(StoragePath(version)).ToString()));
        byte[] index = Encoding.UTF8.GetBytes($$"""
            { "formatVersion": 1, "generated": "{{generated}}", "entries": [
              { "id": "rodaje-transiciones", "version": "{{version}}", "kind": "content", "url": "{{_server.UrlFor(ReleasePath(version))}}", "sha256": "{{sha ?? actual}}", "size": {{package.Length}} } ] }
            """);
        _server.Map("indice.json", TestResponse.File(index));
        _server.Map("indice.json.sig", TestResponse.File(sign is null ? _signer.SignFile(index) : sign(index)));
        return (package, actual);
    }

    private PackageInstaller Installer() => new(PackageLocations.Under(_temp.Combine("complementos")), "rodaje");

    [Fact]
    public async Task Downloads_and_installs_from_the_signed_index()
    {
        (byte[] package, string sha) = Publish();
        using GitHubReleaseDownloader downloader = Downloader();
        var fractions = new List<double>();
        DownloadedPackage downloaded = await downloader.DownloadAsync(Filtros, new SyncProgress(fractions.Add), default);

        Assert.Equal(sha, downloaded.ExpectedSha256);
        Assert.Equal(package, File.ReadAllBytes(downloaded.Path));
        Assert.Equal(1.0, fractions[^1]);
        InstallResult result = Installer().Install(downloaded.Path, downloaded.ExpectedSha256);
        Assert.Equal("1.0.0", result.Package.Manifest.Version.ToString());
        Assert.Equal("1.0.0", (await downloader.AvailableVersionsAsync(default))["rodaje-transiciones"].ToString());
    }

    [Theory]
    [InlineData("otra-clave")]
    [InlineData("otros-datos")]
    [InlineData("no-es-base64")]
    [InlineData("vacia")]
    [InlineData("demasiado-larga")]
    public async Task A_bad_signature_or_wrong_key_downloads_nothing(string kind)
    {
        Func<byte[], byte[]> sign = kind switch
        {
            "otra-clave" => index => Encoding.ASCII.GetBytes(TestIndexSigner.SignWithForeignKey(index)),
            "otros-datos" => index => Encoding.ASCII.GetBytes(_signer.SignOtherData(index)),
            "no-es-base64" => _ => "esto no es una firma!"u8.ToArray(),
            "vacia" => _ => [],
            _ => index => [.. _signer.SignFile(index), .. new byte[IndexSignature.MaxSignatureBytes]],
        };
        Publish(sign: sign);
        using GitHubReleaseDownloader downloader = Downloader();
        await Assert.ThrowsAsync<DownloadVerificationException>(() => downloader.DownloadAsync(Filtros, null, default));
        Assert.Equal(1, _server.RequestCount("indice.json.sig"));
        Assert.DoesNotContain(_server.Requests, r => r.Path.EndsWith(".paquete", StringComparison.Ordinal));
        Assert.Empty(_storage.Requests);
        Assert.False(Directory.Exists(Downloads) && Directory.EnumerateFiles(Downloads, "*.paquete*").Any());
    }

    [Fact]
    public async Task Every_wrong_hash_in_the_index_is_rejected_and_deleted()
    {
        var random = new Random(5);
        for (int i = 0; i < 100; i++)
        {
            (_, string sha) = Publish();
            char[] chars = sha.ToCharArray();
            int at = random.Next(chars.Length);
            chars[at] = chars[at] == '0' ? '1' : '0';
            Publish(sha: new string(chars));
            using GitHubReleaseDownloader downloader = Downloader();
            await Assert.ThrowsAsync<DownloadVerificationException>(() => downloader.DownloadAsync(Filtros, null, default));
            Assert.Empty(Directory.EnumerateFiles(Downloads, "*.paquete*"));
        }
    }

    [Fact]
    public async Task An_older_index_than_one_already_seen_is_refused()
    {
        Publish(generated: "2026-10-11T12:00:00Z");
        using (GitHubReleaseDownloader first = Downloader())
        {
            await first.GetIndexAsync();
        }

        Publish(generated: "2026-09-01T00:00:00Z");
        using GitHubReleaseDownloader second = Downloader();
        await Assert.ThrowsAsync<DownloadVerificationException>(() => second.GetIndexAsync());
    }

    [Fact]
    public async Task Without_a_key_it_is_unavailable()
    {
        using GitHubReleaseDownloader downloader = Downloader(publicKey: null);
        Assert.False(downloader.IsAvailable);
        Assert.NotNull(downloader.UnavailableReason);
        await Assert.ThrowsAsync<PackageDownloadException>(() => downloader.DownloadAsync(Filtros, null, default));
    }

    [Fact]
    public async Task An_unpublished_package_or_one_for_a_newer_app_says_so()
    {
        Publish();
        using GitHubReleaseDownloader downloader = Downloader();
        CatalogEntry missing = Filtros with { Id = "rodaje-titulos", Name = "Títulos" };
        PackageDownloadException ex = await Assert.ThrowsAsync<PackageDownloadException>(() => downloader.DownloadAsync(missing, null, default));
        Assert.Contains("no está publicado", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_power_cut_at_any_point_never_leaves_a_half_installed_package()
    {
        // Versión 1 instalada; la 2 se baja y se instala con un «apagón» en cada etapa. Tras cada corte, lo que el programa
        // ve al arrancar es la 1 o la 2 completas, y repetir la operación termina con la 2.
        PackageInstaller installer = Installer();
        (byte[] v1, _) = Publish("1.0.0");
        File.WriteAllBytes(_temp.Combine("v1.paquete"), v1);
        installer.Install(_temp.Combine("v1.paquete"));
        (byte[] v2, string sha2) = Publish("2.0.0");
        PackageLocations locations = installer.Locations;
        string target = Path.Combine(locations.Packages, "rodaje-transiciones");

        // 1. Corte a mitad de la descarga: queda el parcial; el paquete instalado sigue siendo el 1. El servidor corta
        // cuando el descargador ya guardó la mitad (CutSignal), así el parcial mide lo mismo en Linux y en Windows.
        int half = v2.Length / 2;
        var cutSignal = new CutSignal();
        _storage.Map(StoragePath("2.0.0"),
            TestResponse.File(v2, $"\"{sha2}\"") with { CutAfterBytes = half, CutSignal = cutSignal },
            TestResponse.File(v2, $"\"{sha2}\""));
        var halfway = new SyncProgress(fraction =>
        {
            if (fraction >= half / (double)v2.Length)
            {
                cutSignal.Release();
            }
        });
        using (GitHubReleaseDownloader cut = new(Options(policy: DownloadUrlPolicy.WithLoopback()), _http) { Files = new FileDownloader(_http) { MaxAttempts = 1 } })
        {
            await cut.GetIndexAsync();
            await Assert.ThrowsAsync<PackageDownloadException>(() => cut.DownloadAsync(Filtros, halfway, default));
        }

        Assert.True(File.Exists(Path.Combine(Downloads, sha2 + ".paquete" + FileDownloader.PartialSuffix)));
        Assert.Equal("1.0.0", Installed(locations));

        // 2. Corte durante la extracción (carpeta .instalando- a medias) y 3. entre apartar la 1 y poner la 2.
        Directory.CreateDirectory(Path.Combine(locations.Packages, ".instalando-abcdef123456", "transiciones"));
        File.WriteAllText(Path.Combine(locations.Packages, ".instalando-abcdef123456", "transiciones", "deslizar.json"), "{");
        Directory.Move(target, Path.Combine(locations.Packages, ".anterior-0123456789ab-rodaje-transiciones"));
        Assert.Null(Installed(locations));

        // Al arrancar: se resuelven los restos (vuelve la 1) y la descarga sigue desde el parcial.
        Assert.Equal(2, installer.CleanUpLeftovers());
        Assert.Equal("1.0.0", Installed(locations));
        Assert.Equal(["rodaje-transiciones"], Directory.GetDirectories(locations.Packages).Select(Path.GetFileName));

        using GitHubReleaseDownloader again = Downloader();
        DownloadedPackage downloaded = await again.DownloadAsync(Filtros, null, default);
        Assert.Equal($"bytes={half}-", _storage.Requests[^1].Range);
        Assert.Equal(206, _storage.Requests[^1].StatusCode);
        installer.Install(downloaded.Path, downloaded.ExpectedSha256);
        Assert.Equal("2.0.0", Installed(locations));
        Assert.Equal(v2, File.ReadAllBytes(downloaded.Path));
    }

    private static string? Installed(PackageLocations locations) =>
        PackageLibrary.Scan(locations, "rodaje").Find("rodaje-transiciones")?.Manifest.Version.ToString();

    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
