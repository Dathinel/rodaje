using System.Globalization;
using System.IO.Compression;
using System.Text;
using Rodaje.Packages.Download;
using Rodaje.TestServer;

namespace Rodaje.Packages.Tests;

/// <summary>
/// Familia 7 (Paquetes): Rutas y cultura:
/// carpetas con espacios, tildes y «ñ», y con la cultura del hilo en es-CO y tr-TR
/// (comparaciones de nombres y hashes en mayúsculas y minúsculas sin defecto de la «i» turca).
/// </summary>
public sealed class PathsAndCulturePackagesTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly HttpClient _http = new();

    public void Dispose()
    {
        _http.Dispose();
        _folder.Dispose();
    }

    private static string Zip(string path, params (string Name, string Content)[] entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.Create);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach ((string name, string content) in entries)
        {
            ZipArchiveEntry entry = zip.CreateEntry(name);
            using Stream s = entry.Open();
            s.Write(Encoding.UTF8.GetBytes(content));
        }
        return path;
    }

    private static string ContentManifest(string id = "paquete-cultura", string version = "1.0.0") =>
        $$"""
        {
          "formatVersion": 1,
          "id": "{{id}}",
          "version": "{{version}}",
          "kind": "content",
          "name": "Transición de Diseño Español",
          "author": "Diseñador Ñoño",
          "description": "Una descripción con acentos: á é í ó ú y la letra eñe: ñ.",
          "license": "MIT",
          "repository": "https://github.com/Dathinel/{{id}}",
          "apps": ["rodaje"],
          "items": [
            { "id": "diseno-1", "kind": "transition", "name": "Diseño Uno", "file": "transiciones/diseño.json" }
          ]
        }
        """;

    [Theory]
    [InlineData("es-CO")]
    [InlineData("tr-TR")]
    public void RutasYCultura_InstalacionEnRutaConEspaciosTildesYEne_Pasa(string cultureName)
    {
        using (CultureScope.Set(cultureName))
        {
            // Carpeta con espacios, tildes y eñes
            string root = _folder.Combine("rodaje diseño y edición", "año 2026", "carpeta con ñ");
            var locations = PackageLocations.Under(root);
            var installer = new PackageInstaller(locations, "rodaje");

            string pkg = Zip(_folder.Combine("cultura.paquete"),
                ("paquete.json", ContentManifest("pkg-cultura", "1.0.0")),
                ("transiciones/diseño.json", "{\"nombre\": \"fundido\"}"));

            InstallResult res = installer.Install(pkg);
            Assert.Equal(InstallOutcome.Installed, res.Outcome);
            string targetDir = res.Package.Directory;

            PackageIntegrity.Verify(targetDir);

            PackageScan scan = PackageLibrary.Scan(locations, "rodaje");
            InstalledPackage? found = scan.Find("pkg-cultura");
            Assert.NotNull(found);
            Assert.Equal("Transición de Diseño Español", found.Manifest.Name);

            installer.Uninstall("pkg-cultura");
            Assert.False(Directory.Exists(targetDir));
        }
    }

    [Theory]
    [InlineData("es-CO")]
    [InlineData("tr-TR")]
    public void RutasYCultura_PackageIntegrity_ArchivosConTildesYEne_Pasa(string cultureName)
    {
        using (CultureScope.Set(cultureName))
        {
            string dir = _folder.Combine("verif-cultura-" + cultureName, "canción y diseño");
            Directory.CreateDirectory(dir);

            File.WriteAllText(Path.Combine(dir, "canción.wav"), "audio");
            File.WriteAllText(Path.Combine(dir, "diseño_año.json"), "{}");
            File.WriteAllText(Path.Combine(dir, "niño.txt"), "texto");

            PackageIntegrity.Write(dir);
            PackageIntegrity.Verify(dir);

            // Si se altera un byte de niño.txt, la verificación lo detecta sin fallo de cultura
            File.WriteAllText(Path.Combine(dir, "niño.txt"), "alterado");
            var ex = Assert.Throws<PackageInstallException>(() => PackageIntegrity.Verify(dir));
            Assert.Contains("niño.txt", ex.Message, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("es-CO")]
    [InlineData("tr-TR")]
    public void RutasYCultura_SafePaths_AceptaTildesYEne(string cultureName)
    {
        using (CultureScope.Set(cultureName))
        {
            Assert.True(SafePaths.TryNormalizeRelative("transiciones/diseño_año_2026.json", out string? norm1));
            Assert.Equal("transiciones/diseño_año_2026.json", norm1);

            Assert.True(SafePaths.TryNormalizeRelative("fuentes/árbol/peña.ttf", out string? norm2));
            Assert.Equal("fuentes/árbol/peña.ttf", norm2);
        }
    }

    [Theory]
    [InlineData("es-CO")]
    [InlineData("tr-TR")]
    public void RutasYCultura_NombresConI_SeComparanSinLaITurca(string cultureName)
    {
        using (CultureScope.Set(cultureName))
        {
            // En tr-TR «I» en minúscula es «ı» e «i» en mayúscula es «İ»: comparando con la cultura del hilo estos nombres no serían iguales.
            string dir = _folder.Combine("i-turca-" + cultureName);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "inicio.ini"), "[x]");
            string sha = PackageInstaller.ComputeSha256(Path.Combine(dir, "inicio.ini"));
            Assert.True(SignedIndex.IsSha256(sha));
            File.WriteAllText(Path.Combine(dir, PackageIntegrity.FileName), $$"""{ "INICIO.INI": "{{sha}}" }""");
            PackageIntegrity.Verify(dir);

            var installer = new PackageInstaller(PackageLocations.Under(_folder.Combine("complementos-" + cultureName)), "rodaje");
            string pkg = Zip(_folder.Combine("i-" + cultureName + ".paquete"),
                ("paquete.json", ContentManifest()),
                ("transiciones/diseño.json", "{}"),
                ("TRANSICIONES/DISEÑO.JSON", "{}"));
            var ex = Assert.Throws<PackageInstallException>(() => installer.Install(pkg));
            Assert.Contains("repite el archivo", ex.Message, StringComparison.Ordinal);

            Assert.Equal("titulo-inicial", PackageLibrary.ToIdentifier("TÍTULO INICIAL"));
            Assert.True(SafePaths.IsReservedName("NUL.INI"));
            Assert.False(Identifiers.IsValid("lpt1.ini"));
        }
    }

    [Theory]
    [InlineData("es-CO")]
    [InlineData("tr-TR")]
    public async Task RutasYCultura_ModelStore_ColocarYBuscarEnRutaConAcentos(string cultureName)
    {
        using (CultureScope.Set(cultureName))
        {
            await using var server = TestHttpServer.Start();
            byte[] data = TestPayloads.Deterministic(1000, seed: 77);
            string sha = TestPayloads.Sha256Hex(data);
            server.Map("pesos.bin", TestResponse.File(data));

            string storeFolder = _folder.Combine("almacén de modelos", "año 2026");
            var store = new ModelStore(storeFolder, new FileDownloader(_http, SignedIndex.MaxEntrySize), DownloadUrlPolicy.WithLoopback());

            var model = new ModuleModel("pesos", "pesos/modelo.onnx", data.Length, sha, server.UrlFor("pesos.bin"), "MIT");
            string moduleDir = _folder.Combine("módulo con acento", "instalación");

            string placed = await store.PlaceAsync(model, moduleDir, null, default);
            Assert.True(File.Exists(placed));
            Assert.True(model.IsPresentIn(moduleDir));

            string? found = store.Find(model.Spec);
            Assert.NotNull(found);
            Assert.True(File.Exists(found));
        }
    }

    [Theory]
    [InlineData("es-CO")]
    [InlineData("tr-TR")]
    public void RutasYCultura_PackageCatalog_TextosConTildesYEne(string cultureName)
    {
        using (CultureScope.Set(cultureName))
        {
            string json = """
            {
              "formatVersion": 1,
              "apps": [
                { "id": "rodaje", "name": "Rodaje Edición" }
              ],
              "entries": [
                {
                  "id": "transiciones-pro",
                  "name": "Transición Profesional: Niño y Ñandú",
                  "description": "Efectos ópticos con desenfoque de movimiento y viñeta.",
                  "repository": "https://github.com/Dathinel/transiciones-pro",
                  "apps": ["rodaje"],
                  "provides": ["disolución", "cortinilla"]
                }
              ]
            }
            """;

            PackageCatalog catalog = PackageCatalog.Parse(json);
            CatalogEntry? entry = catalog.Find("transiciones-pro");
            Assert.NotNull(entry);
            Assert.Equal("Transición Profesional: Niño y Ñandú", entry.Name);
            Assert.Equal("Efectos ópticos con desenfoque de movimiento y viñeta.", entry.Description);
        }
    }

    [Theory]
    [InlineData("es-CO")]
    [InlineData("tr-TR")]
    public void RutasYCultura_SpanishText_Comparer_OrdenaBien(string cultureName)
    {
        using (CultureScope.Set(cultureName))
        {
            string[] words = ["zorro", "ñu", "oso", "nube", "Ñandú", "nz"];
            var sorted = words.OrderBy(w => w, SpanishText.Comparer).ToArray();

            // En español la «ñ» va después de toda la «n» y antes de la «o»; por código iría detrás de la «z».
            Assert.Equal(["nube", "nz", "Ñandú", "ñu", "oso", "zorro"], sorted);
        }
    }

    [Theory]
    [InlineData("es-CO")]
    [InlineData("tr-TR")]
    public async Task RutasYCultura_FileDownloader_DescargaEnRutaConEspaciosYEne(string cultureName)
    {
        using (CultureScope.Set(cultureName))
        {
            await using var server = TestHttpServer.Start();
            byte[] data = TestPayloads.Deterministic(5000, seed: 99);
            server.Map("descarga-cultura", TestResponse.File(data));

            string dest = _folder.Combine("descargas con espacios y tildes", "año 2026", "archivo con ñ.paquete");
            var downloader = new FileDownloader(_http, SignedIndex.MaxEntrySize);

            await downloader.DownloadAsync(new DownloadTarget(server.UrlFor("descarga-cultura"), TestPayloads.Sha256Hex(data), data.Length), dest, null, default);

            Assert.True(File.Exists(dest));
            Assert.Equal(data, File.ReadAllBytes(dest));
        }
    }

    [Theory]
    [InlineData("es-CO")]
    [InlineData("tr-TR")]
    public void RutasYCultura_CleanUpLeftovers_EnRutaConAcentos(string cultureName)
    {
        using (CultureScope.Set(cultureName))
        {
            string root = _folder.Combine("restos con acentos", "diseño 2026");
            var locations = PackageLocations.Under(root);
            var installer = new PackageInstaller(locations, "rodaje");

            Directory.CreateDirectory(locations.Packages);
            string staging = Path.Combine(locations.Packages, ".instalando-123456789012");
            Directory.CreateDirectory(staging);
            File.WriteAllText(Path.Combine(staging, "resto.tmp"), "resto");

            int cleaned = installer.CleanUpLeftovers();
            Assert.Equal(1, cleaned);
            Assert.False(Directory.Exists(staging));
        }
    }

    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo _prevCulture;
        private readonly CultureInfo _prevUiCulture;

        private CultureScope(string cultureName)
        {
            _prevCulture = CultureInfo.CurrentCulture;
            _prevUiCulture = CultureInfo.CurrentUICulture;
            var culture = new CultureInfo(cultureName);
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
        }

        public static CultureScope Set(string cultureName) => new(cultureName);

        public void Dispose()
        {
            CultureInfo.CurrentCulture = _prevCulture;
            CultureInfo.CurrentUICulture = _prevUiCulture;
        }
    }
}
