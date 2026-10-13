using Rodaje.Packages.Download;
using Rodaje.TestServer;

namespace Rodaje.Packages.Tests.Download;

/// <summary>
/// Contra el catálogo publicado de verdad (Dathinel/rodaje-catalogo) y la clave pública que lleva el exe. Necesita red,
/// así que la CI no la corre: <c>dotnet test --filter Categoria=Red</c>.
/// </summary>
[Trait("Categoria", "Red")]
public sealed class CatalogoRealTests : IDisposable
{
    private static readonly Uri IndexUrl = new("https://github.com/Dathinel/rodaje-catalogo/releases/latest/download/indice.json");

    private readonly TempFolder _temp = new();
    private readonly HttpClient _http = new();

    public CatalogoRealTests() => _http.DefaultRequestHeaders.UserAgent.ParseAdd("Rodaje-pruebas");

    public void Dispose()
    {
        _http.Dispose();
        _temp.Dispose();
    }

    private static string PublicKey()
    {
        string? directory = AppContext.BaseDirectory;
        const string relative = "src/Rodaje.App/Services/Packages/clave-catalogo.txt";
        while (directory is not null && !File.Exists(Path.Combine(directory, relative)))
        {
            directory = Path.GetDirectoryName(directory);
        }

        return File.ReadAllText(Path.Combine(directory ?? throw new InvalidOperationException("No encuentro clave-catalogo.txt."), relative)).Trim();
    }

    private GitHubReleaseDownloader Downloader(Uri indexUrl, DownloadUrlPolicy? policy = null) => new(new GitHubReleaseOptions
    {
        IndexUrl = indexUrl,
        PublicKey = PublicKey(),
        DownloadFolder = _temp.Combine("descargas"),
        AppVersion = PackageVersion.Parse("0.7.0"),
        Policy = policy ?? DownloadUrlPolicy.Default,
    }, _http);

    [RedTheory]
    [InlineData("rodaje-transiciones", "Transiciones", 45)]
    [InlineData("rodaje-filtros", "Filtros", 26)]
    public async Task Baja_verifica_e_instala_desde_el_catalogo_publicado(string id, string name, int items)
    {
        using GitHubReleaseDownloader downloader = Downloader(IndexUrl);
        Assert.True(downloader.IsAvailable);
        SignedIndex index = await downloader.GetIndexAsync();
        Assert.Empty(downloader.IndexProblems);

        var entry = new CatalogEntry(id, PackageKind.Content, name, "", new Uri($"https://github.com/Dathinel/{id}"), ["rodaje"], []);
        DownloadedPackage downloaded = await downloader.DownloadAsync(entry, null, default);
        Assert.Equal(index.Latest(id, PackageVersion.Parse("0.7.0"))!.Sha256, downloaded.ExpectedSha256);

        var installer = new PackageInstaller(PackageLocations.Under(_temp.Combine("complementos")), "rodaje");
        InstallResult result = installer.Install(downloaded.Path, downloaded.ExpectedSha256);
        Assert.Equal(id, result.Package.Manifest.Id);
        Assert.Equal(items, result.Package.Manifest.Items.Count);
        Assert.True(Directory.Exists(result.Package.Directory));
    }

    [RedFact]
    public async Task Rechaza_el_indice_real_con_la_firma_o_el_contenido_alterados()
    {
        byte[] json = await _http.GetByteArrayAsync(IndexUrl);
        byte[] signature = await _http.GetByteArrayAsync(new Uri(IndexUrl.AbsoluteUri + ".sig"));
        using var key = System.Security.Cryptography.ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(PublicKey()), out _);
        Assert.NotNull(SignedIndex.Load(json, signature, key, DownloadUrlPolicy.Default));

        byte[] badSignature = (byte[])signature.Clone();
        badSignature[badSignature.Length / 2] = (byte)(badSignature[badSignature.Length / 2] == (byte)'A' ? 'B' : 'A');
        byte[] badJson = (byte[])json.Clone();
        int digit = Array.IndexOf(badJson, (byte)'1');
        badJson[digit] = (byte)'2';

        Assert.Throws<DownloadVerificationException>(() => SignedIndex.Load(json, badSignature, key, DownloadUrlPolicy.Default));
        Assert.Throws<DownloadVerificationException>(() => SignedIndex.Load(badJson, signature, key, DownloadUrlPolicy.Default));

        await using var server = TestHttpServer.Start();
        server.Map("indice.json", TestResponse.File(json));
        server.Map("indice.json.sig", TestResponse.File(badSignature));
        using GitHubReleaseDownloader downloader = Downloader(server.UrlFor("indice.json"));
        await Assert.ThrowsAsync<DownloadVerificationException>(() => downloader.GetIndexAsync());
    }
}

/// <summary>Las pruebas contra GitHub solo corren con <c>RODAJE_PRUEBAS_RED=1</c>: así la CI no depende de la red.</summary>
internal static class RedSkip
{
    public static string? Reason =>
        Environment.GetEnvironmentVariable("RODAJE_PRUEBAS_RED") == "1" ? null : "Contra GitHub: solo con RODAJE_PRUEBAS_RED=1.";
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class RedFactAttribute : FactAttribute
{
    public RedFactAttribute() => Skip = RedSkip.Reason;
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class RedTheoryAttribute : TheoryAttribute
{
    public RedTheoryAttribute() => Skip = RedSkip.Reason;
}
