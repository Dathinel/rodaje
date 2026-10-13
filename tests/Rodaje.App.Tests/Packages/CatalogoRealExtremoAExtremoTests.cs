using System.Diagnostics;
using System.Globalization;
using Rodaje.Analysis;
using Rodaje.Analysis.Ducking;
using Rodaje.App.Services.Effects;
using Rodaje.App.Services.Modules;
using Rodaje.App.Services.Packages;
using Rodaje.App.Services.Packages.Download;
using Rodaje.App.Services.Titles;
using Rodaje.App.Tests.Modules;
using Rodaje.App.Tests.Support;
using Rodaje.Core.Model;
using Rodaje.Media.Gpu;
using Rodaje.Packages;
using Rodaje.Packages.Content;
using Rodaje.Packages.Download;
using Rodaje.Playback.Composition;
using Rodaje.Render.Text;
using Xunit.Abstractions;

namespace Rodaje.App.Tests.Packages;

/// <summary>
/// De punta a punta contra lo publicado (índice firmado de <c>Dathinel/rodaje-catalogo</c>, releases de los complementos,
/// Hugging Face): instalar, usar, actualizar, reanudar y desinstalar como lo hace la página de Complementos. Necesita red:
/// <c>--filter Categoria=Red</c> con <c>RODAJE_PRUEBAS_RED=1</c>. Anota tiempos y tamaños en <c>TestResults\catalogo-real.txt</c>.
/// </summary>
[Trait("Categoria", "Red")]
public sealed partial class CatalogoRealExtremoAExtremoTests(ITestOutputHelper output) : IDisposable
{
    private static readonly string[] Contenido = ["rodaje-fuentes", "rodaje-titulos", "rodaje-zonas"];

    private static readonly string[] Modulos = ["modulo-subtitulos", "modulo-traducir", "modulo-voz", "modulo-audio-ia", "modulo-vision", "modulo-raw"];

    private readonly TempDirectory _folder = new();
    private readonly List<string> _log = [];

    private PackageLocations Locations => PackageLocations.Under(Path.Combine(_folder.Path, "complementos"));

    public void Dispose()
    {
        string results = Path.Combine(RepositoryRoot(), "TestResults");
        Directory.CreateDirectory(results);
        File.AppendAllLines(Path.Combine(results, "catalogo-real.txt"), [$"== {DateTime.Now:yyyy-MM-dd HH:mm}", .. _log]);
        for (int i = 0; ; i++)
        {
            try
            {
                _folder.Dispose();
                return;
            }
            catch (Exception ex) when (i < 30 && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(100);
            }
        }
    }

    private void Note(string text)
    {
        _log.Add(text);
        output.WriteLine(text);
    }

    private PackageService Service() =>
        new(Locations, PackageService.LoadBundledCatalog(), CatalogSource.CreateDownloader(Locations, PublicKey()), models: CatalogSource.CreateModelStore(Locations));

    [RedFact]
    public async Task Instala_usa_actualiza_y_desinstala_desde_el_catalogo_real()
    {
        PackageService service = Service();
        var downloader = Assert.IsType<GitHubReleaseDownloader>(service.Downloader);
        await using ModuleManager modules = new(new ModuleManagerOptions { Roots = [new ModuleRoot(Locations.Modules, ModuleOrigin.Installed)] });
        service.CloseModule = modules.CloseModuleAsync;
        service.Changed += (_, _) => modules.Refresh();

        // El índice firmado nombra todo lo publicado, con la versión más nueva de cada uno.
        SignedIndex index = await downloader.GetIndexAsync();
        Assert.Empty(downloader.IndexProblems);
        IReadOnlyDictionary<string, PackageVersion> available = await service.AvailableVersionsAsync();
        Assert.Null(service.UpdatesProblem);
        Assert.Equal("1.2.0", available["rodaje-transiciones"].ToString());
        Assert.Equal("1.1.0", available["rodaje-filtros"].ToString());
        Assert.All(Contenido.Concat(Modulos), id => Assert.Equal(id == "modulo-traducir" ? "1.0.1" : "1.0.0", available[id].ToString()));
        Assert.All(service.Catalog.Entries, e => Assert.True(available.ContainsKey(e.Id), $"{e.Id} no está publicado."));
        Note($"Índice {index.Generated:O}: {index.Entries.Count} entradas.");

        // Transiciones y filtros 1.0.0 (bajadas con el hash del índice) y actualización a la más nueva desde el catálogo.
        foreach ((string id, int items, string latest) in new[] { ("rodaje-transiciones", 45, "1.2.0"), ("rodaje-filtros", 26, "1.1.0") })
        {
            IndexEntry old = index.Entries.Single(e => e.Id == id && e.Version.ToString() == "1.0.0");
            string path = Path.Combine(_folder.Path, $"{id}-1.0.0.paquete");
            await downloader.Files.DownloadAsync(new DownloadTarget(old.Url, old.Sha256, old.Size), path, null, default);
            Assert.Equal(InstallOutcome.Installed, (await service.InstallFromFileAsync(path, old.Sha256)).Outcome);
            Assert.True(service.HasUpdate(service.Entries().Single(e => e.Id == id)));

            var watch = Stopwatch.StartNew();
            InstallResult updated = await service.InstallFromCatalogAsync(service.Catalog.Find(id)!);
            Assert.Equal((InstallOutcome.Updated, "1.0.0", latest), (updated.Outcome, updated.PreviousVersion?.ToString(), updated.Package.Manifest.Version.ToString()));
            Assert.Equal(items, updated.Package.Manifest.Items.Count);
            Assert.False(service.HasUpdate(service.Entries().Single(e => e.Id == id)));
            Note($"{id}: 1.0.0 → {latest} ({Size(Latest(index, id).Size)}) en {watch.Elapsed.TotalSeconds:0.0} s, {items} elementos.");
        }

        foreach (string id in Contenido)
        {
            var watch = Stopwatch.StartNew();
            InstallResult result = await service.InstallFromCatalogAsync(service.Catalog.Find(id)!);
            Assert.Equal(InstallOutcome.Installed, result.Outcome);
            Note($"{id} {result.Package.Manifest.Version}: {Size(Latest(index, id).Size)} en {watch.Elapsed.TotalSeconds:0.0} s, {result.Package.Manifest.Items.Count} elementos.");
        }

        // Un estilo de título aplicado: el karaoke trae su fuente (Montserrat), su resaltado y su origen.
        var loader = new InstalledContentLoader(() => service.Installed);
        const string Karaoke = "rodaje-titulos@1.0.0/karaoke-pop";
        TextStyle karaoke = TitleStyles.FromPreset(Assert.IsType<TextStylePreset>(loader.LoadTextStyle(Karaoke)), Karaoke);
        Assert.Equal(("Montserrat", 800, "rodaje-fuentes@1.0.0/montserrat"), (karaoke.Font.Family, karaoke.Font.Weight, karaoke.Font.Source));
        Assert.Equal("#FFD400", karaoke.Highlight?.Color);
        Assert.Equal(Karaoke, karaoke.Source);
        Assert.NotNull(karaoke.In);

        // Las fuentes del paquete se registran en la colección privada y DirectWrite las resuelve por su nombre.
        IReadOnlyList<string> fontFiles = TitleFontFiles.Find(service.Installed);
        Assert.NotEmpty(fontFiles);
        Assert.All(fontFiles, f => Assert.StartsWith(service.Installed.Find("rodaje-fuentes")!.Directory, f, StringComparison.OrdinalIgnoreCase));
        using (GpuDevice gpu = GpuDevice.Create())
        using (var titles = new TitleRasterizer(gpu, new TitleRasterizerOptions { BackgroundThread = false }))
        {
            // Sin registrar, DirectWrite no las tiene (salvo que Windows ya las traiga instaladas): las da el paquete.
            int before = new[] { "Montserrat", "Nunito" }.Count(f => titles.FindMissingFonts([Content(f, new TextStyle { Font = new StyleFont { Family = f } })]).Count > 0);
            Note($"Antes de registrar las fuentes del paquete, DirectWrite no encontraba {before} de 2 (Montserrat, Nunito).");
            Assert.Empty(titles.RegisterFonts(fontFiles));
            foreach (string family in new[] { "Montserrat", "Nunito" })
            {
                TextStyle style = TitleStyles.Default();
                style.Font = new StyleFont { Family = family, Weight = 700 };
                Assert.Empty(titles.FindMissingFonts([Content(family, style)]));
                Assert.False(titles.Rasterize(Content(family, style))!.FontMissing, family);
            }

            Assert.Empty(titles.FindMissingFonts([Content("Karaoke", karaoke)]));
            Note($"Fuentes: {fontFiles.Count} archivos registrados; Montserrat y Nunito resueltas por DirectWrite.");
        }

        // Un módulo: se instala, baja sus modelos (releases de modulo-audio-ia) y detecta la voz de un archivo de prueba.
        var stages = new List<InstallProgress>();
        var moduleWatch = Stopwatch.StartNew();
        InstallResult audio = await service.InstallFromCatalogAsync(service.Catalog.Find("modulo-audio-ia")!, new Collect(stages.Add));
        Assert.Empty(service.MissingModelsOf("modulo-audio-ia"));
        IReadOnlyList<ModuleModel> models = ModuleModels.Read(Path.Combine(audio.Package.Directory, PackageManifest.FileName));
        Assert.Equal(4, models.Count);
        Assert.All(models, m => Assert.True(m.IsPresentIn(audio.Package.Directory), m.Id));
        Note($"modulo-audio-ia: paquete {Size(Latest(index, "modulo-audio-ia").Size)} + modelos {Size(models.Sum(m => m.Size))} en {moduleWatch.Elapsed.TotalSeconds:0.0} s ({stages.Select(s => s.Stage).Distinct().Count()} etapas).");

        modules.Refresh();
        var failures = new List<ModuleFailure>();
        modules.Failed += (_, f) => failures.Add(f);
        var detector = new ModuleVoiceDetector(modules, new EnergyVoiceDetector());
        Assert.True(detector.UsesModule, string.Join(" ", modules.Problems));
        IVoiceActivitySession session = detector.Begin(48_000, 2);
        session.Push(TestVoice(out double voiceStart, out double voiceEnd));
        var useWatch = Stopwatch.StartNew();
        IReadOnlyList<TimeRange> ranges = await session.FinishAsync();
        Assert.Empty(failures.Select(f => f.Message));
        Assert.NotEmpty(ranges);
        Assert.True(ranges.All(r => r.Start >= voiceStart - 0.1 && r.End <= voiceEnd + 0.1) && ranges.Sum(r => r.Duration) > (voiceEnd - voiceStart) / 2,
            string.Join(", ", ranges));
        Note($"detectar-voz: {ranges.Count} tramos de voz en {useWatch.Elapsed.TotalSeconds:0.0} s (incluye abrir el módulo).");

        // Desinstalar el módulo con su proceso abierto: se cierra antes, la carpeta se va y sus modelos dejan el almacén.
        int pid = Assert.Single(modules.ProcessIds);
        await service.UninstallAsync("modulo-audio-ia");
        Assert.True(await EchoModules.HasExitedAsync(pid));
        Assert.False(Directory.Exists(audio.Package.Directory));
        Assert.All(models, m => Assert.False(File.Exists(Path.Combine(service.Models.Folder, m.Sha256)), m.Id));

        foreach (string id in Contenido.Concat(["rodaje-transiciones", "rodaje-filtros"]))
        {
            await service.UninstallAsync(id);
        }

        Assert.DoesNotContain(service.Installed.Packages, p => p.Origin == PackageOrigin.Installed);
        Note("Desinstalados los 6 complementos (uno, el módulo, con su proceso abierto).");
    }

    /// <summary>
    /// Cada módulo publicado se baja del catálogo, se instala (sin sus modelos, para no bajar 3,5 GB) y su proceso abre y
    /// saluda con su id y la versión 1.0.0; el exe va en la raíz, donde busca <c>modelos\</c>.
    /// </summary>
    [RedFact]
    public async Task Cada_modulo_publicado_se_instala_y_saluda()
    {
        PackageService service = Service();
        var downloader = Assert.IsType<GitHubReleaseDownloader>(service.Downloader);
        await using ModuleManager modules = new(new ModuleManagerOptions { Roots = [new ModuleRoot(Locations.Modules, ModuleOrigin.Installed)] });
        service.CloseModule = modules.CloseModuleAsync;
        foreach (string id in Modulos)
        {
            var watch = Stopwatch.StartNew();
            DownloadedPackage package = await downloader.DownloadAsync(service.Catalog.Find(id)!, null, default);
            long size = new FileInfo(package.Path).Length;
            InstallResult result = await service.InstallFromFileAsync(package.Path, package.ExpectedSha256);
            File.Delete(package.Path);
            Assert.Equal(id, result.Package.Id);
            Assert.DoesNotContain('/', result.Package.Manifest.Module!.Entry);
            modules.Refresh();
            Rodaje.Modules.Protocol.ModuleInfo info = await modules.RunAsync(id, "Saludo", call => Task.FromResult(call.Info));
            Assert.Equal((id, result.Package.Manifest.Version.ToString()), (info.Id, info.Version));
            Note($"{id}: {Size(size)}, {info.Version}, instalado y saludando en {watch.Elapsed.TotalSeconds:0.0} s; le faltan {service.MissingModelsOf(id).Count} modelos ({Size(service.MissingModelsOf(id).Sum(m => m.Size))}).");
            await service.UninstallAsync(id);
            Assert.False(Directory.Exists(result.Package.Directory));
        }
    }

    /// <summary>Una descarga real cortada a mitad sigue desde donde quedó: solo se piden los bytes que faltan.</summary>
    [RedFact]
    public async Task Una_descarga_cortada_sigue_desde_donde_quedo()
    {
        PackageService service = Service();
        var downloader = Assert.IsType<GitHubReleaseDownloader>(service.Downloader);
        IndexEntry fonts = Latest(await downloader.GetIndexAsync(), "rodaje-fuentes");
        using var cut = new CancellationTokenSource();
        var watch = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.InstallFromCatalogAsync(service.Catalog.Find("rodaje-fuentes")!,
            new Collect(p =>
            {
                if (p.Fraction > 0.4)
                {
                    cut.Cancel();
                }
            }), cut.Token));
        string partial = Path.Combine(CatalogSource.DownloadFolder(Locations), fonts.Sha256 + PackageManifest.FileExtension + FileDownloader.PartialSuffix);
        long kept = new FileInfo(partial).Length;
        Assert.InRange(kept, 1, fonts.Size - 1);
        long before = downloader.Files.BytesReceived;

        InstallResult result = await service.InstallFromCatalogAsync(service.Catalog.Find("rodaje-fuentes")!);

        long resumed = downloader.Files.BytesReceived - before;
        Assert.Equal(fonts.Size - kept, resumed);
        Assert.Equal("rodaje-fuentes", result.Package.Id);
        Assert.False(File.Exists(partial));
        Note($"rodaje-fuentes ({Size(fonts.Size)}): cortada en {Size(kept)}, reanudada con {Size(resumed)} en {watch.Elapsed.TotalSeconds:0.0} s.");
        await service.UninstallAsync("rodaje-fuentes");
    }

    /// <summary>Los modelos de cada módulo publicado se pueden bajar: cada URL del índice responde con el tamaño firmado.</summary>
    [RedFact]
    public async Task Cada_modelo_publicado_responde_con_su_tamano()
    {
        PackageService service = Service();
        var downloader = Assert.IsType<GitHubReleaseDownloader>(service.Downloader);
        SignedIndex index = await downloader.GetIndexAsync();
        using HttpClient http = CatalogSource.CreateHttpClient();
        IndexEntry[] models = [.. index.Entries.Where(e => e.Kind == IndexEntryKind.Model)];
        Assert.NotEmpty(models);
        foreach (IndexEntry model in models.DistinctBy(m => m.Sha256))
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, model.Url);
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
            using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            Assert.True(response.IsSuccessStatusCode, $"{model.Id}: {(int)response.StatusCode}");
            Assert.Equal(model.Size, response.Content.Headers.ContentRange?.Length);
        }

        Note($"{models.DistinctBy(m => m.Sha256).Count()} modelos publicados, {Size(models.DistinctBy(m => m.Sha256).Sum(m => m.Size))} en total, todos con el tamaño firmado.");
    }

    private static IndexEntry Latest(SignedIndex index, string id) => index.Latest(id, CatalogSource.AppVersion)!;

    private static GeneratedContent Content(string text, TextStyle style) =>
        new(GeneratorKinds.Text, text, style, 0, 1920, 1080, 1) { ClipId = "t" };

    private static string Size(long bytes) => bytes >= 1 << 20
        ? (bytes / (double)(1 << 20)).ToString("0.0", CultureInfo.InvariantCulture) + " MB"
        : (bytes / 1024.0).ToString("0", CultureInfo.InvariantCulture) + " KB";

    /// <summary>Estéreo de 48 kHz: 3 s de silencio y 12 s de la voz real de prueba (o una sintética si no está el corpus).</summary>
    private static float[] TestVoice(out double start, out double end)
    {
        start = 3;
        end = 15;
        float[] clip;
        if (AudioAiCorpus.Voice is { } voice)
        {
            (float[] samples, _) = AudioAiCorpus.ReadMono(voice);
            clip = samples.AsSpan(20 * 48_000, 12 * 48_000).ToArray();
        }
        else
        {
            clip = new float[12 * 48_000];
            for (int i = 0; i < clip.Length; i++)
            {
                double t = i / 48_000.0;
                clip[i] = (float)(0.3 * (Math.Sin(2 * Math.PI * 180 * t) + (0.5 * Math.Sin(2 * Math.PI * 900 * t))) * (0.6 + (0.4 * Math.Sin(2 * Math.PI * 4 * t))));
            }
        }

        var stereo = new float[(clip.Length + (48_000 * 3)) * 2];
        for (int i = 0; i < clip.Length; i++)
        {
            stereo[(i + (48_000 * 3)) * 2] = stereo[((i + (48_000 * 3)) * 2) + 1] = clip[i];
        }

        return stereo;
    }

    private static string PublicKey() =>
        File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Rodaje.App", "Services", "Packages", "clave-catalogo.txt")).Trim();

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Rodaje.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException("No encuentro la raíz del repositorio.");
    }

    private sealed class Collect(Action<InstallProgress> report) : IProgress<InstallProgress>
    {
        public void Report(InstallProgress value) => report(value);
    }
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class RedFactAttribute : FactAttribute
{
    public RedFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("RODAJE_PRUEBAS_RED") != "1")
        {
            Skip = "Contra GitHub y Hugging Face: solo con RODAJE_PRUEBAS_RED=1.";
        }
    }
}
