using System.IO.Compression;
using System.Net;
using System.Text;
using Rodaje.App.Services.Packages;
using Rodaje.App.Tests.Support;
using Rodaje.Packages;
using Rodaje.Packages.Download;
using Rodaje.TestServer;

namespace Rodaje.App.Tests.Packages;

public sealed class LocalPackageInstallTests : IDisposable
{
    private readonly TempDirectory _folder = new();

    public void Dispose() => _folder.Dispose();

    [Theory]
    [InlineData("module", true, true, LocalInstallOutcome.Installed)]
    [InlineData("module", false, true, LocalInstallOutcome.NeedsConfirmation)]
    [InlineData("content", true, true, LocalInstallOutcome.NeedsConfirmation)]
    [InlineData("module", true, false, LocalInstallOutcome.NeedsConfirmation)]
    public void Only_a_matching_module_in_a_verified_index_installs_directly(string indexKind, bool matchingHash, bool validSignature,
        LocalInstallOutcome expected) => UiThread.Run(async () =>
    {
        string path = WriteArchive();
        string hash = matchingHash ? PackageInstaller.ComputeSha256(path) : new string('0', 64);
        byte[] json = Encoding.UTF8.GetBytes($$"""
            { "formatVersion": 1, "generated": "2026-01-01T00:00:00Z", "entries": [
              { "id": "modulo-prueba", "version": "1.0.0", "kind": "{{indexKind}}",
                "url": "https://github.com/Dathinel/modulo-prueba/releases/download/v1.0.0/prueba.paquete",
                "sha256": "{{hash}}", "size": {{new FileInfo(path).Length}} } ] }
            """);
        using var signer = new TestIndexSigner();
        byte[] signature = validSignature ? signer.SignFile(json) : Encoding.ASCII.GetBytes(signer.SignOtherData(json));
        using var http = new HttpClient(new IndexHandler(json, signature));
        using var downloader = new GitHubReleaseDownloader(new GitHubReleaseOptions
        {
            IndexUrl = new Uri("https://github.com/Dathinel/rodaje-catalogo/releases/download/v8/indice.json"),
            PublicKey = signer.PublicKeyBase64,
            DownloadFolder = Path.Combine(_folder.Path, "descargas"),
            AppVersion = PackageVersion.Parse("1.0.0"),
        }, http);
        PackageService service = Create(downloader);

        LocalPackageInstall result = await service.InstallLocalFileAsync(path);

        Assert.Equal(expected, result.Outcome);
        Assert.Equal(expected == LocalInstallOutcome.Installed, Directory.Exists(Path.Combine(service.Locations.Modules, "modulo-prueba")));
    });

    [Theory]
    [InlineData(true, LocalInstallOutcome.Installed)]
    [InlineData(false, LocalInstallOutcome.Cancelled)]
    public void An_unlisted_module_needs_an_explicit_decision(bool accepted, LocalInstallOutcome expected) => UiThread.Run(async () =>
    {
        PackageService service = Create();
        string path = WriteArchive();
        int confirmations = 0;
        service.CloseModule = _ => throw new InvalidOperationException("No se debe cerrar un módulo antes de confirmar.");
        Assert.Equal(LocalInstallOutcome.NeedsConfirmation, (await service.InstallLocalFileAsync(path)).Outcome);
        service.CloseModule = null;

        LocalPackageInstall result = await service.InstallLocalFileAsync(path, () =>
        {
            confirmations++;
            Assert.False(Directory.Exists(Path.Combine(service.Locations.Modules, "modulo-prueba")));
            return Task.FromResult(accepted);
        });

        Assert.Equal(1, confirmations);
        Assert.Equal(expected, result.Outcome);
        Assert.Equal(accepted, result.Result is not null);
        Assert.Equal(accepted, Directory.Exists(Path.Combine(service.Locations.Modules, "modulo-prueba")));
    });

    [Fact]
    public void Confirming_one_file_does_not_authorize_a_replacement() => UiThread.Run(async () =>
    {
        PackageService service = Create();
        string path = WriteArchive();

        await Assert.ThrowsAsync<PackageInstallException>(() => service.InstallLocalFileAsync(path, () =>
        {
            File.AppendAllText(path, "cambio");
            return Task.FromResult(true);
        }));

        Assert.False(Directory.Exists(Path.Combine(service.Locations.Modules, "modulo-prueba")));
    });

    [Theory]
    [InlineData(false, LocalInstallOutcome.Installed)]
    [InlineData(true, LocalInstallOutcome.NeedsConfirmation)]
    public void Content_only_needs_confirmation_if_it_declares_a_program(bool declaresEntry, LocalInstallOutcome expected) => UiThread.Run(async () =>
    {
        PackageService service = Create();
        string path = WriteArchive(content: true, declaresEntry: declaresEntry);

        LocalPackageInstall result = await service.InstallLocalFileAsync(path);

        Assert.Equal(expected, result.Outcome);
    });

    private PackageService Create(IPackageDownloader? downloader = null) => new(
        PackageLocations.Under(Path.Combine(_folder.Path, "complementos")), PackageService.LoadBundledCatalog(),
        downloader ?? new UnavailablePackageDownloader("Sin índice."));

    private string WriteArchive(bool content = false, bool declaresEntry = true)
    {
        string path = Path.Combine(_folder.Path, "prueba.paquete");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var executable = new StreamWriter(zip.CreateEntry("prueba.exe").Open())) executable.Write("programa de prueba; no se ejecuta");
        using var writer = new StreamWriter(zip.CreateEntry("paquete.json").Open());
        string module = declaresEntry ? """, "module": { "entry": "prueba.exe", "protocol": 1, "functions": ["prueba"] }""" : "";
        writer.Write($$"""
            { "formatVersion": 1, "id": "modulo-prueba", "version": "1.0.0", "kind": "{{(content ? "content" : "module")}}",
              "name": "Prueba", "author": "Prueba", "license": "MIT", "apps": ["rodaje"],
              "items": [{ "id": "elemento", "kind": "text-style", "name": "Elemento" }] {{module}} }
            """);
        return path;
    }

    private sealed class IndexHandler(byte[] json, byte[] signature) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(request.RequestUri!.AbsolutePath.EndsWith(".sig", StringComparison.Ordinal) ? signature : json),
            });
    }
}
