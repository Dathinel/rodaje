using System.Diagnostics;
using System.Net;
using Rodaje.Packages.Download;
using Rodaje.TestServer;

namespace Rodaje.Packages.Tests.Hardened;

/// <summary>
/// Ataques al ciclo completo de un módulo con modelos (instalar, colocar, verificar, actualizar, desinstalar) y a las
/// rutas que Windows interpreta por su cuenta: uniones, solo lectura, flujos alternos, punto o espacio final y nombres 8.3.
/// </summary>
public sealed class PackageLifecycleAttackTests : IAsyncLifetime, IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly TempFolder _outside = new();
    private readonly TestHttpServer _server = TestHttpServer.Start();
    private readonly HttpClient _http = new();
    private readonly List<string> _junctions = [];
    private readonly PackageLocations _locations;
    private readonly PackageInstaller _installer;

    public PackageLifecycleAttackTests()
    {
        _locations = PackageLocations.Under(_folder.Combine("complementos"));
        _installer = new PackageInstaller(_locations, "rodaje");
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        Assert.Empty(_server.Faults);
    }

    public void Dispose()
    {
        foreach (string junction in _junctions.Where(Directory.Exists))
        {
            using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c rmdir \"{junction}\"") { CreateNoWindow = true, UseShellExecute = false })!;
            process.WaitForExit();
        }

        _http.Dispose();
        foreach (TempFolder folder in new[] { _folder, _outside })
        {
            foreach (string file in Directory.EnumerateFiles(folder.Path, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            folder.Dispose();
        }
    }

    private ModelStore Store() =>
        new(_folder.Combine("complementos", "modelos"), new FileDownloader(_http, SignedIndex.MaxEntrySize) { RetryDelay = TimeSpan.FromMilliseconds(5) }, DownloadUrlPolicy.WithLoopback());

    private (string Json, byte[] Data) Model(string id, string file, int seed, int length = 4096)
    {
        byte[] data = TestPayloads.Deterministic(length, seed);
        _server.Map(id, TestResponse.File(data));
        return ($$"""{ "id": "{{id}}", "file": "{{file}}", "size": {{data.Length}}, "sha256": "{{TestPayloads.Sha256Hex(data)}}", "license": "MIT", "url": "{{_server.UrlFor(id)}}" }""", data);
    }

    private string ModulePackage(string id, string version, params string[] models) =>
        Samples.Zip(_folder.Combine($"{id}-{version}-{Guid.NewGuid():N}.paquete"),
            ("paquete.json", Samples.Manifest(id, version, kind: "module", items: "[]",
                extra: $$""", "module": { "entry": "bin/modulo.exe", "protocol": 1, "functions": ["procesar"], "models": [{{string.Join(",", models)}}] }""")),
            ("bin/modulo.exe", "MZ " + version));

    private static IReadOnlyList<ModuleModel> ModelsOf(string moduleDirectory) =>
        ModuleModels.Read(Path.Combine(moduleDirectory, PackageManifest.FileName));

    /// <summary>Lo que hay bajo complementos\modulos y complementos\paquetes, restos incluidos.</summary>
    private string[] Installed() =>
        [.. new[] { _locations.Modules, _locations.Packages }.Where(Directory.Exists)
            .SelectMany(Directory.EnumerateFileSystemEntries).Select(e => Path.GetFileName(e)).Order(StringComparer.Ordinal)];

    private void Junction(string link, string target)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"mklink /J falló ({process.ExitCode})");
        _junctions.Add(link);
    }

    [Fact]
    public async Task Reinstalar_recoloca_el_modelo_sin_volver_a_bajarlo_y_desinstalar_no_deja_nada()
    {
        (string json, byte[] data) = Model("red", "modelos/red.onnx", seed: 1);
        string directory = _installer.Install(ModulePackage("modulo-ciclo", "1.0.0", json)).Package.Directory;
        ModelStore store = Store();
        ModuleModel model = Assert.Single(ModelsOf(directory));

        await store.PlaceAsync(model, directory, null, default);
        PackageIntegrity.Verify(directory);

        Assert.Equal(InstallOutcome.Reinstalled, _installer.Install(ModulePackage("modulo-ciclo", "1.0.0", json)).Outcome);
        Assert.False(model.IsPresentIn(directory));
        PackageIntegrity.Verify(directory);

        await store.PlaceAsync(model, directory, null, default);
        Assert.Equal(data, File.ReadAllBytes(model.PathIn(directory)));
        PackageIntegrity.Verify(directory);
        Assert.Equal(1, _server.RequestCount("red"));

        _installer.Uninstall("modulo-ciclo");
        Assert.Empty(Installed());
        Assert.Equal(data.Length, store.Prune([]));
    }

    [Fact]
    public async Task Actualizar_con_otro_modelo_en_la_misma_ruta_coloca_el_nuevo_y_nunca_deja_el_viejo()
    {
        (string oldJson, _) = Model("red-v1", "modelos/red.onnx", seed: 2);
        (string newJson, byte[] newData) = Model("red-v2", "modelos/red.onnx", seed: 3);
        string directory = _installer.Install(ModulePackage("modulo-cambia", "1.0.0", oldJson)).Package.Directory;
        ModelStore store = Store();
        await store.PlaceAsync(Assert.Single(ModelsOf(directory)), directory, null, default);

        Assert.Equal(InstallOutcome.Updated, _installer.Install(ModulePackage("modulo-cambia", "2.0.0", newJson)).Outcome);
        ModuleModel current = Assert.Single(ModelsOf(directory));

        // Mismo tamaño y otra huella: lo viejo no puede pasar por presente tras actualizar.
        Assert.False(current.IsPresentIn(directory));
        await store.PlaceAsync(current, directory, null, default);
        Assert.Equal(newData, File.ReadAllBytes(current.PathIn(directory)));
        PackageIntegrity.Verify(directory);

        _installer.Uninstall("modulo-cambia");
        Assert.Empty(Installed());
    }

    [Fact]
    public async Task Dos_modulos_comparten_un_modelo_y_desinstalar_uno_no_rompe_el_otro()
    {
        (string json, byte[] data) = Model("comun", "modelos/comun.onnx", seed: 4);
        string first = _installer.Install(ModulePackage("modulo-uno", "1.0.0", json)).Package.Directory;
        string second = _installer.Install(ModulePackage("modulo-dos", "1.0.0", json)).Package.Directory;
        ModelStore store = Store();
        ModuleModel model = Assert.Single(ModelsOf(first));
        await store.PlaceAsync(model, first, null, default);
        await store.PlaceAsync(model, second, null, default);
        Assert.Equal(1, _server.RequestCount("comun"));

        _installer.Uninstall("modulo-uno");

        Assert.Equal(["modulo-dos"], Installed());
        Assert.Equal(data, File.ReadAllBytes(model.PathIn(second)));
        PackageIntegrity.Verify(second);
        Assert.Equal(0, store.Prune([model.Sha256]));
        Assert.Equal(data, File.ReadAllBytes(Path.Combine(store.Folder, model.Sha256)));
    }

    [WindowsOnlyFact("las uniones de directorio son una funcionalidad de NTFS")]
    public async Task Una_union_dentro_del_modulo_no_se_sigue_al_colocar_verificar_actualizar_ni_desinstalar()
    {
        string victim = Path.Combine(_outside.Path, "victima.txt");
        File.WriteAllText(victim, "no me toques");
        (string json, _) = Model("red-union", "modelos/red.onnx", seed: 5);
        string package = ModulePackage("modulo-union", "1.0.0", json);
        string directory = _installer.Install(package).Package.Directory;
        Junction(Path.Combine(directory, "modelos"), _outside.Path);
        ModuleModel model = Assert.Single(ModelsOf(directory));

        var placed = await Assert.ThrowsAsync<PackageDownloadException>(() => Store().PlaceAsync(model, directory, null, default));
        Assert.Contains("fuera de la carpeta del módulo", placed.Message, StringComparison.Ordinal);
        Assert.Equal(["victima.txt"], Directory.EnumerateFileSystemEntries(_outside.Path).Select(e => Path.GetFileName(e)));

        var verified = Assert.Throws<PackageInstallException>(() => PackageIntegrity.Verify(directory));
        Assert.Contains("modelos/victima.txt", verified.Message, StringComparison.Ordinal);

        Assert.Equal(InstallOutcome.Reinstalled, _installer.Install(package).Outcome);
        Assert.Equal("no me toques", File.ReadAllText(victim));

        Junction(Path.Combine(directory, "modelos"), _outside.Path);
        _installer.Uninstall("modulo-union");
        Assert.Empty(Installed());
        Assert.Equal("no me toques", File.ReadAllText(victim));
    }

    [WindowsOnlyFact("las uniones de directorio son una funcionalidad de NTFS")]
    public void La_limpieza_de_restos_no_sigue_una_union_con_nombre_de_resto()
    {
        string victim = Path.Combine(_outside.Path, "victima.txt");
        File.WriteAllText(victim, "no me toques");
        Directory.CreateDirectory(_locations.Packages);
        Directory.CreateDirectory(_locations.Modules);
        Junction(Path.Combine(_locations.Packages, ".instalando-0123456789ab"), _outside.Path);
        Junction(Path.Combine(_locations.Modules, ".desinstalando-0123456789ab"), _outside.Path);

        Assert.Equal(2, _installer.CleanUpLeftovers());

        Assert.Empty(Installed());
        Assert.Equal("no me toques", File.ReadAllText(victim));
    }

    [Fact]
    public void Un_archivo_de_solo_lectura_no_deja_restos_al_actualizar_ni_al_desinstalar()
    {
        string package = Samples.ValidPackage(_folder.Combine("a.paquete"));
        string directory = _installer.Install(package).Package.Directory;
        File.SetAttributes(Path.Combine(directory, "transiciones", "deslizar.json"), FileAttributes.ReadOnly);

        Assert.Equal(InstallOutcome.Reinstalled, _installer.Install(package).Outcome);
        Assert.Equal(["rodaje-transiciones"], Installed());

        File.SetAttributes(Path.Combine(directory, "transiciones", "deslizar.json"), FileAttributes.ReadOnly | FileAttributes.Hidden);
        _installer.Uninstall("rodaje-transiciones");
        Assert.Empty(Installed());
    }

    [Theory]
    [InlineData("bin/modulo.exe")]
    [InlineData("paquete.json")]
    [InlineData("integridad.json")]
    [InlineData("BIN/MODULO.EXE")]
    public async Task Un_modelo_declarado_sobre_un_archivo_del_paquete_nunca_queda_verificado_con_otro_contenido(string file)
    {
        (string json, byte[] data) = Model("pisa", file, seed: 6);
        string package = ModulePackage("modulo-pisa", "1.0.0", json);
        string directory;
        try
        {
            directory = _installer.Install(package).Package.Directory;
        }
        catch (PackageInstallException)
        {
            Assert.Empty(Installed());
            return;
        }

        foreach (ModuleModel model in ModelsOf(directory))
        {
            try
            {
                await Store().PlaceAsync(model, directory, null, default);
            }
            catch (PackageDownloadException)
            {
            }
        }

        string target = Path.Combine(directory, file.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(target) && File.ReadAllBytes(target).AsSpan().SequenceEqual(data))
        {
            Assert.Throws<PackageInstallException>(() => PackageIntegrity.Verify(directory));
        }
        else
        {
            PackageIntegrity.Verify(directory);
        }
    }

    [Theory]
    [InlineData("transiciones/deslizar.json:flujo")]
    [InlineData("transiciones/deslizar.json::$DATA")]
    [InlineData("transiciones:flujo/otro.json")]
    [InlineData("transiciones./otro.json")]
    [InlineData("transiciones /otro.json")]
    [InlineData("transiciones/deslizar.json.")]
    [InlineData("transiciones/deslizar.json..")]
    [InlineData("transiciones/deslizar.json ")]
    [InlineData("transiciones/.")]
    [InlineData("transiciones/ ")]
    [InlineData("transiciones/.../otro.json")]
    [InlineData("\\\\?\\C:\\Windows\\otro.json")]
    [InlineData("\\\\.\\NUL")]
    [InlineData("transiciones/COM¹")]
    [InlineData("transiciones/conin$")]
    [InlineData("transiciones/nul .json")]
    public void Una_entrada_que_Windows_leeria_como_otra_ruta_se_rechaza_sin_dejar_nada(string entry)
    {
        string package = Samples.Zip(_folder.Combine("hostil.paquete"),
            ("paquete.json", Samples.Manifest()), ("transiciones/deslizar.json", "{}"), (entry, "hostil"));

        var ex = Assert.Throws<PackageInstallException>(() => _installer.Install(package));

        Assert.Contains("ruta no permitida", ex.Message, StringComparison.Ordinal);
        Assert.Empty(Installed());
    }

    [Theory]
    [InlineData("TRANSI~1/deslizar.json")]
    [InlineData("PAQUET~1.JSO")]
    [InlineData("transiciones/DESLIZ~1.JSO")]
    public void Un_nombre_corto_8_3_nunca_pisa_el_archivo_al_que_apunta(string alias)
    {
        string package = Samples.Zip(_folder.Combine("corto.paquete"),
            ("paquete.json", Samples.Manifest()), ("transiciones/deslizar.json", "{}"), (alias, "hostil"));

        string directory;
        try
        {
            directory = _installer.Install(package).Package.Directory;
        }
        catch (PackageInstallException)
        {
            // Con nombres cortos activos en el volumen el alias ya existe y la entrada no puede crearse.
            Assert.Empty(Installed());
            return;
        }

        Assert.Equal("{}", File.ReadAllText(Path.Combine(directory, "transiciones", "deslizar.json")));
        Assert.True(ManifestReader.ReadFile(Path.Combine(directory, "paquete.json")).IsValid);
        PackageIntegrity.Verify(directory);
    }

    [Theory]
    [InlineData("integridad.json")]
    [InlineData("INTEGRIDAD.JSON")]
    [InlineData("envoltura/integridad.json")]
    public void La_integridad_que_trae_el_paquete_no_vale_se_calcula_al_instalar(string entry)
    {
        bool wrapped = entry.Contains('/', StringComparison.Ordinal);
        string prefix = wrapped ? "envoltura/" : "";
        string package = Samples.Zip(_folder.Combine("integridad.paquete"),
            (prefix + "paquete.json", Samples.Manifest()),
            (prefix + "transiciones/deslizar.json", "{}"),
            (entry, $$"""{ "paquete.json": "{{new string('0', 64)}}" }"""));

        string directory = _installer.Install(package).Package.Directory;

        PackageIntegrity.Verify(directory);
        Assert.Equal(["integridad.json", "paquete.json", "transiciones"],
            Directory.EnumerateFileSystemEntries(directory).Select(e => Path.GetFileName(e).ToLowerInvariant()).Order(StringComparer.Ordinal));
        File.AppendAllText(Path.Combine(directory, "paquete.json"), " ");
        Assert.Throws<PackageInstallException>(() => PackageIntegrity.Verify(directory));
    }

    [Theory]
    [InlineData("datos", "datos/a.txt")]
    [InlineData("datos/a.txt", "datos")]
    [InlineData("datos", "Datos/a.txt")]
    [InlineData("Datos/", "datos")]
    [InlineData("paquete.json/", "paquete.json/x.txt")]
    public void Un_archivo_y_una_carpeta_con_el_mismo_nombre_se_rechazan_sin_dejar_nada(string first, string second)
    {
        string package = Samples.Zip(_folder.Combine("choque.paquete"),
            ("paquete.json", Samples.Manifest()), ("transiciones/deslizar.json", "{}"), (first, "uno"), (second, "dos"));

        Assert.Throws<PackageInstallException>(() => _installer.Install(package));

        Assert.Empty(Installed());
    }

    [Theory]
    [InlineData("a.txt:flujo")]
    [InlineData("a.txt::$DATA")]
    [InlineData("a.txt.")]
    [InlineData("a.txt ")]
    [InlineData(" a.txt")]
    [InlineData("./a.txt")]
    [InlineData("sub/../a.txt")]
    [InlineData("sub//a.txt")]
    [InlineData("\\\\?\\a.txt")]
    public void Una_clave_de_integridad_que_Windows_resolveria_al_mismo_archivo_se_rechaza(string key)
    {
        // Windows abre «a.txt.», «a.txt » y «a.txt::$DATA» como a.txt: con la huella correcta pasarían por buenas
        // y el mismo archivo quedaría anotado bajo dos nombres.
        string directory = _folder.Combine("integridad");
        Directory.CreateDirectory(Path.Combine(directory, "sub"));
        File.WriteAllText(Path.Combine(directory, "a.txt"), "contenido");
        string sha = PackageInstaller.ComputeSha256(Path.Combine(directory, "a.txt"));
        File.WriteAllText(Path.Combine(directory, PackageIntegrity.FileName),
            $$"""{ "a.txt": "{{sha}}", "{{key.Replace("\\", "\\\\", StringComparison.Ordinal)}}": "{{sha}}" }""");

        var ex = Assert.Throws<PackageInstallException>(() => PackageIntegrity.Verify(directory));

        Assert.Contains("entrada no válida", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "a.txt": 5 }""")]
    [InlineData("""{ "a.txt": null }""")]
    [InlineData("""{ "a.txt": ["x"] }""")]
    [InlineData("""[ "a.txt" ]""")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{ "integridad.json": "0000000000000000000000000000000000000000000000000000000000000000" }""")]
    public void Un_manifiesto_de_integridad_con_otra_forma_no_da_por_bueno_ningun_archivo(string json)
    {
        string directory = _folder.Combine("forma");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "a.txt"), "contenido");
        File.WriteAllText(Path.Combine(directory, PackageIntegrity.FileName), json);

        Assert.Throws<PackageInstallException>(() => PackageIntegrity.Verify(directory));
    }

    [Fact]
    public void Un_archivo_sobrante_oculto_y_de_sistema_tambien_cuenta()
    {
        string directory = _folder.Combine("oculto");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "a.txt"), "contenido");
        PackageIntegrity.Write(directory);
        string hidden = Path.Combine(directory, "version.dll");
        File.WriteAllText(hidden, "colada");
        File.SetAttributes(hidden, FileAttributes.Hidden | FileAttributes.System);

        var ex = Assert.Throws<PackageInstallException>(() => PackageIntegrity.Verify(directory));

        Assert.Contains("version.dll", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../fuera.onnx")]
    [InlineData("modelos/../../fuera.onnx")]
    [InlineData("modelos/red.onnx:flujo")]
    [InlineData("modelos/red.onnx.")]
    [InlineData("modelos/nul")]
    public async Task Un_modelo_armado_a_mano_con_una_ruta_hostil_no_se_coloca(string file)
    {
        (_, byte[] data) = Model("hostil", "modelos/red.onnx", seed: 7);
        string directory = _folder.Combine("modulo");
        Directory.CreateDirectory(Path.Combine(directory, "modelos"));
        File.WriteAllText(Path.Combine(directory, "modelos", "red.onnx"), "firmado");
        var model = new ModuleModel("hostil", file, data.Length, TestPayloads.Sha256Hex(data), _server.UrlFor("hostil"), "MIT");

        await Assert.ThrowsAsync<PackageDownloadException>(() => Store().PlaceAsync(model, directory, null, default));

        Assert.Equal(["modelos/red.onnx"], Directory.EnumerateFiles(_folder.Path, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(directory, f).Replace('\\', '/')));
        Assert.Equal("firmado", File.ReadAllText(Path.Combine(directory, "modelos", "red.onnx")));
        Assert.False(File.Exists(Path.Combine(directory, "modelos", "red.onnx:flujo")));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../paquetes/vecino")]
    [InlineData("vecino/..")]
    [InlineData("vecino\\")]
    [InlineData("vecino.")]
    [InlineData("vecino ")]
    [InlineData("VECINO")]
    [InlineData("vecino:flujo")]
    [InlineData("VECINO~1")]
    [InlineData(".operacion.lock")]
    [InlineData("nul")]
    [InlineData("*")]
    [InlineData("")]
    public void Desinstalar_con_un_id_hostil_no_borra_nada(string id)
    {
        Directory.CreateDirectory(Path.Combine(_locations.Packages, "vecino"));
        File.WriteAllText(Path.Combine(_locations.Packages, "vecino", "a.txt"), "sigue");

        var ex = Assert.Throws<PackageInstallException>(() => _installer.Uninstall(id));

        Assert.Contains("no es un paquete que se pueda desinstalar", ex.Message, StringComparison.Ordinal);
        Assert.Equal("sigue", File.ReadAllText(Path.Combine(_locations.Packages, "vecino", "a.txt")));
    }

    [Fact]
    public async Task Un_servidor_que_no_dice_el_tamano_y_no_para_de_mandar_se_corta_en_el_tamano_publicado()
    {
        byte[] data = TestPayloads.Deterministic(100_000, seed: 8);
        using var http = new HttpClient(new EndlessHandler());
        var downloader = new FileDownloader(http, SignedIndex.MaxEntrySize);
        string destination = _folder.Combine("sin-fin.paquete");

        var ex = await Assert.ThrowsAsync<DownloadVerificationException>(() =>
            downloader.DownloadAsync(new DownloadTarget(new Uri("https://ejemplo.invalid/sin-fin"), TestPayloads.Sha256Hex(data), data.Length), destination, null, default));

        Assert.Contains("más bytes de los publicados", ex.Message, StringComparison.Ordinal);
        Assert.InRange(downloader.BytesReceived, data.Length + 1, data.Length + (1 << 16));
        Assert.Empty(Directory.EnumerateFiles(_folder.Path, "sin-fin*"));
    }

    [Fact]
    public async Task Una_redireccion_a_otro_servidor_con_otro_contenido_no_deja_nada_en_el_destino()
    {
        await using TestHttpServer other = TestHttpServer.Start();
        byte[] published = TestPayloads.Deterministic(30_000, seed: 9);
        other.Map("suplantado", TestResponse.File(TestPayloads.Deterministic(30_000, seed: 10)));
        _server.Map("publicado", TestResponse.Redirect(other.UrlFor("suplantado").ToString()));
        var downloader = new FileDownloader(_http, SignedIndex.MaxEntrySize);
        string destination = _folder.Combine("redirigido.paquete");

        var ex = await Assert.ThrowsAsync<DownloadVerificationException>(() =>
            downloader.DownloadAsync(new DownloadTarget(_server.UrlFor("publicado"), TestPayloads.Sha256Hex(published), published.Length), destination, null, default));

        Assert.Contains("no es el publicado", ex.Message, StringComparison.Ordinal);
        Assert.Equal(1, other.RequestCount("suplantado"));
        Assert.Empty(Directory.EnumerateFiles(_folder.Path, "redirigido*"));
    }

    /// <summary>Responde 200 sin <c>Content-Length</c> y con un cuerpo que no termina.</summary>
    private sealed class EndlessHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new EndlessStream()) });
    }

    private sealed class EndlessStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Array.Fill(buffer, (byte)7, offset, count);
            return count;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
