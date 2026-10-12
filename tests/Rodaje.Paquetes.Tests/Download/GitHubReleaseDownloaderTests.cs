using System.Security.Cryptography;
using System.Text;
using Rodaje.Paquetes.Download;

namespace Rodaje.Paquetes.Tests.Download;

public sealed class GitHubReleaseDownloaderTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly LocalHttpServer _server = new();
    private readonly HttpClient _http = new();
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public void Dispose()
    {
        _http.Dispose();
        _server.Dispose();
        _key.Dispose();
        _temp.Dispose();
    }

    private static readonly CatalogEntry Filtros = new("rodaje-transiciones", PackageKind.Content, "Transiciones", "", new Uri("https://github.com/Dathinel/rodaje-transiciones"), ["rodaje"], []);

    private string Downloads => _temp.Combine("descargas");

    private GitHubReleaseDownloader Downloader(string? publicKey = "") => new(new GitHubReleaseOptions
    {
        IndexUrl = new Uri(_server.BaseUrl, "indice.json"),
        PublicKey = publicKey == "" ? Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo()) : publicKey,
        DownloadFolder = Downloads,
        AppVersion = PackageVersion.Parse("0.7.0"),
        Policy = DownloadUrlPolicy.WithLoopback("Dathinel"),
    }, _http)
    {
        Files = new FileDownloader(_http) { RetryDelay = TimeSpan.FromMilliseconds(10) },
    };

    /// <summary>Publica un paquete y un índice que lo lista con el hash dado (por defecto, el del archivo).</summary>
    private (byte[] Package, string Sha) Publish(string version = "1.0.0", string? sha = null, string generated = "2026-10-11T12:00:00Z", ECDsa? signer = null)
    {
        string path = Samples.ValidPackage(_temp.Combine($"p-{version}.paquete"), version: version);
        byte[] package = File.ReadAllBytes(path);
        string actual = Convert.ToHexStringLower(SHA256.HashData(package));
        Uri url = _server.Put($"v{version}/rodaje-transiciones.paquete", package);
        byte[] index = Encoding.UTF8.GetBytes($$"""
            { "formatVersion": 1, "generated": "{{generated}}", "entries": [
              { "id": "rodaje-transiciones", "version": "{{version}}", "kind": "content", "url": "{{url}}", "sha256": "{{sha ?? actual}}", "size": {{package.Length}} } ] }
            """);
        _server.Put("indice.json", index);
        _server.Put("indice.json.sig", IndexSignature.Sign(index, signer ?? _key));
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

    [Fact]
    public async Task A_bad_signature_or_wrong_key_downloads_nothing()
    {
        using ECDsa other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Publish(signer: other);
        using GitHubReleaseDownloader downloader = Downloader();
        await Assert.ThrowsAsync<DownloadVerificationException>(() => downloader.DownloadAsync(Filtros, null, default));
        Assert.DoesNotContain(_server.Requests, r => r.Path.EndsWith(".paquete", StringComparison.Ordinal));
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

        // 1. Corte a mitad de la descarga: queda el parcial; el paquete instalado sigue siendo el 1.
        using (GitHubReleaseDownloader cut = new(new GitHubReleaseOptions
        {
            IndexUrl = new Uri(_server.BaseUrl, "indice.json"), PublicKey = Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo()),
            DownloadFolder = Downloads, AppVersion = PackageVersion.Parse("0.7.0"), Policy = DownloadUrlPolicy.WithLoopback(),
        }, _http) { Files = new FileDownloader(_http) { MaxAttempts = 1 } })
        {
            await cut.GetIndexAsync();
            _server.CutAfter.Enqueue(v2.Length / 2);
            await Assert.ThrowsAsync<PackageDownloadException>(() => cut.DownloadAsync(Filtros, null, default));
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
        Assert.Equal($"bytes={v2.Length / 2}-", _server.Requests.Last().Range);
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
