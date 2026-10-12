using System.IO.Compression;
using Rodaje.App.Services.Modules;
using Rodaje.App.Tests.Support;
using Rodaje.App.Views.Export;
using Rodaje.Export.Encoders;
using Rodaje.Media;
using Rodaje.Media.Export;
using Rodaje.Media.Proxies;
using Xunit.Abstractions;

namespace Rodaje.App.Tests.Modules;

/// <summary>
/// El complemento que arma <c>build\armar-codificadores-gpl.ps1</c>, instalado en una carpeta de módulos:
/// <c>RODAJE_CODIFICADORES_GPL</c> apunta al <c>.paquete</c> (si no, el de <c>dist\complementos</c>). Sin él, las
/// pruebas que lo necesitan lo dicen y no comprueban nada.
/// </summary>
public sealed class GplEncoderPackTests(ITestOutputHelper output) : IDisposable
{
    private readonly TempDirectory _folder = new();

    public void Dispose() => _folder.Dispose();

    private static string? Package()
    {
        if (Environment.GetEnvironmentVariable("RODAJE_CODIFICADORES_GPL") is { Length: > 0 } custom && File.Exists(custom))
        {
            return custom;
        }

        string? modules = ModuleCatalog.FindDevelopmentModules(AppContext.BaseDirectory);
        string dist = Path.Combine(Path.GetDirectoryName(modules ?? AppContext.BaseDirectory)!, "dist", "complementos");
        return Directory.Exists(dist) ? Directory.GetFiles(dist, "rodaje-codificadores-gpl-*.paquete").Order().LastOrDefault() : null;
    }

    private string Install(string package)
    {
        string root = Path.Combine(_folder.Path, "modulos");
        ZipFile.ExtractToDirectory(package, Path.Combine(root, GplEncoderPack.Id));
        return root;
    }

    [Fact]
    public void Without_the_pack_nothing_is_found_and_protocol_0_is_not_a_module()
    {
        string root = Path.Combine(_folder.Path, "modulos");
        Assert.Null(GplEncoderPack.FindExecutable([new ModuleRoot(root, ModuleOrigin.Installed)]));
        string dir = Path.Combine(root, GplEncoderPack.Id, "bin");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "ffmpeg.exe"), [0x4D, 0x5A]);
        File.WriteAllText(Path.Combine(root, GplEncoderPack.Id, "paquete.json"), """
            { "formatVersion": 1, "id": "rodaje-codificadores-gpl", "version": "0.1.0", "kind": "module", "name": "GPL", "author": "Pruebas",
              "license": "GPL-3.0-or-later", "apps": ["rodaje"], "module": { "entry": "bin/ffmpeg.exe", "protocol": 0, "functions": ["libx264"] } }
            """);
        var problems = new List<string>();
        Assert.Empty(ModuleCatalog.Scan([new ModuleRoot(root, ModuleOrigin.Installed)], problems));
        Assert.Empty(problems);
        Assert.NotNull(GplEncoderPack.FindExecutable([new ModuleRoot(root, ModuleOrigin.Installed)]));
        // Un exe que no arranca no pasa la verificación: no se ofrece nada.
        Assert.Null(GplEncoderPack.TryOpen([new ModuleRoot(root, ModuleOrigin.Installed)], out string? problem));
        Assert.NotNull(problem);
    }

    [Fact]
    public async Task The_built_pack_offers_x264_and_x265_through_its_own_ffmpeg()
    {
        if (Package() is not { } package || !FfmpegLibrary.TryInitialize(out _))
        {
            output.WriteLine("Sin el .paquete de armar-codificadores-gpl.ps1 (o sin FFmpeg): se omite.");
            return;
        }

        string root = Install(package);
        FfmpegExecutable gpl = GplEncoderPack.TryOpen([new ModuleRoot(root, ModuleOrigin.Installed)], out string? problem)
            ?? throw new Xunit.Sdk.XunitException(problem);
        output.WriteLine(gpl.Version);
        Assert.True(gpl.IsGplAddOn);
        Assert.Throws<FfmpegNotFoundException>(() => FfmpegExecutable.Open(gpl.Path));
        Assert.Same(gpl, gpl.EnsureUnchanged());

        CatalogEncoderSource source = CatalogEncoderSource.ForGplAddOn(gpl);
        IReadOnlyList<EncoderSpec> probed = await source.GetAsync();
        Assert.Equal(["libx264", "libx265"], probed.Select(e => e.Id));

        var runner = new AppExportRunner(null, null) { FindGplFfmpeg = () => gpl };
        IReadOnlyList<EncoderSpec> all = await runner.GetEncodersAsync();
        EncoderSpec x264 = Assert.Single(all, e => e.Id == "libx264");
        Assert.Equal(EncoderLicense.GplAddOn, x264.License);
        Assert.False(x264.InDefaults);
        Assert.Equal("x264 (H.264, complemento GPL)", ExportEncoderCatalog.DisplayName(x264));
        Assert.NotEqual("libx264", ExportChoices.DefaultEncoder(ExportCodec.H264, all)?.Id);
        Assert.Contains(x264, ExportChoices.EncodersFor(ExportCodec.H264, all));

        var without = new AppExportRunner(null, null) { FindGplFfmpeg = () => null };
        Assert.DoesNotContain(await without.GetEncodersAsync(), e => e.License == EncoderLicense.GplAddOn);
    }
}
