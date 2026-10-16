using System.Buffers.Binary;
using System.IO.Compression;

namespace Rodaje.Packages.Tests;

/// <summary>
/// Contrato de la sección «Instalar y desinstalar» de <c>docs/formato-paquete.md</c> (lo que toca a
/// <see cref="PackageInstaller"/> y <see cref="SafePaths"/>): una prueba por promesa, con la frase del documento encima.
/// </summary>
public sealed class InstallContractTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly PackageLocations _locations;
    private readonly PackageInstaller _installer;

    public InstallContractTests()
    {
        _locations = PackageLocations.Under(_folder.Combine("complementos"));
        _installer = new PackageInstaller(_locations, "rodaje");
    }

    public void Dispose() => _folder.Dispose();

    private string[] PackageFolders() => [.. Directory.GetDirectories(_locations.Packages).Select(d => Path.GetFileName(d)!)];

    private static void WriteEntry(ZipArchive zip, string name, byte[] content, CompressionLevel level = CompressionLevel.Optimal)
    {
        using Stream s = zip.CreateEntry(name, level).Open();
        s.Write(content);
    }

    // «Si se da un SHA-256 esperado (lo da la descarga desde el catálogo), se compara con el del archivo antes de abrirlo.»
    [Fact]
    public void El_sha256_se_compara_antes_de_abrir_el_archivo()
    {
        string notZip = _folder.Combine("no-zip.paquete");
        File.WriteAllText(notZip, "no es un zip");

        var ex = Assert.Throws<PackageInstallException>(() => _installer.Install(notZip, expectedSha256: new string('0', 64)));

        Assert.Contains("no es el esperado", ex.Message, StringComparison.Ordinal);
        Assert.Contains(PackageInstaller.ComputeSha256(notZip), ex.Message, StringComparison.Ordinal);
    }

    // «Se extrae a `paquetes\.instalando-<aleatorio>`.»
    [Fact]
    public void Se_extrae_a_una_carpeta_instalando_aleatoria_dentro_de_paquetes()
    {
        var seen = new List<string>();
        string archive = Samples.ValidPackage(_folder.Combine("t.paquete"));

        _installer.Install(archive, beforeCommit: _ =>
            seen.AddRange(PackageFolders().Where(n => File.Exists(Path.Combine(_locations.Packages, n, "paquete.json")))));
        _installer.Uninstall("rodaje-transiciones");
        _installer.Install(archive, beforeCommit: _ => seen.AddRange(PackageFolders()));

        Assert.Equal(2, seen.Count);
        Assert.All(seen, n => Assert.Matches("^\\.instalando-[0-9a-f]{12}$", n));
        Assert.NotEqual(seen[0], seen[1]);
    }

    // «Cada entrada del zip pasa por `SafePaths`: se rechazan rutas con raíz o unidad, `..` y `.`, segmentos vacíos, dos
    // puntos (unidades y flujos alternos de NTFS), caracteres de control, prohibidos o de dirección (bidi), nombres
    // reservados de Windows (`CON`, `NUL`, `COM1`…, también con extensión) y segmentos que terminan en punto o espacio»
    [Theory]
    [InlineData("/a.json")]
    [InlineData("\\a.json")]
    [InlineData("C:/a.json")]
    [InlineData("C:a.json")]
    [InlineData("a/../b.json")]
    [InlineData("./a.json")]
    [InlineData("a//b.json")]
    [InlineData("a.json:flujo")]
    [InlineData("a\u0001.json")]
    [InlineData("a\u007f.json")]
    [InlineData("a<b.json")]
    [InlineData("a|b.json")]
    [InlineData("a?.json")]
    [InlineData("a*.json")]
    [InlineData("a\".json")]
    [InlineData("a\u202e.json")]
    [InlineData("a\u200f.json")]
    [InlineData("CON")]
    [InlineData("nul")]
    [InlineData("dir/COM1")]
    [InlineData("nul.txt")]
    [InlineData("Com1.tar.gz")]
    [InlineData("lpt9.json")]
    [InlineData("a./b.json")]
    [InlineData("a.json.")]
    [InlineData("a.json ")]
    [InlineData("dir /b.json")]
    public void SafePaths_rechaza_cada_clase_de_ruta_prohibida(string path)
    {
        Assert.False(SafePaths.TryNormalizeRelative(path, out _));

        string archive = Samples.Zip(_folder.Combine("malo.paquete"),
            ("paquete.json", Samples.Manifest()), ("transiciones/deslizar.json", "{}"), (path, "x"));
        Assert.Throws<PackageInstallException>(() => _installer.Install(archive));
        Assert.Empty(PackageFolders());
    }

    // (Lo que sí se acepta: rutas relativas comunes, con ambas barras, que se normalizan a «/».)
    [Theory]
    [InlineData("a.json", "a.json")]
    [InlineData("dir\\sub/a.json", "dir/sub/a.json")]
    [InlineData("console.json", "console.json")]
    [InlineData("com10.json", "com10.json")]
    [InlineData(".oculto", ".oculto")]
    public void SafePaths_acepta_rutas_relativas_comunes(string path, string expected)
    {
        Assert.True(SafePaths.TryNormalizeRelative(path, out string? normalized));
        Assert.Equal(expected, normalized);
    }

    // «Además se comprueba que la ruta final quede dentro de la carpeta»
    [Fact]
    public void La_ruta_final_debe_quedar_dentro_de_la_carpeta()
    {
        string root = _folder.Combine("raiz");

        Assert.True(SafePaths.IsInside(Path.Combine(root, "a", "b.json"), root));
        Assert.False(SafePaths.IsInside(root, root));
        Assert.False(SafePaths.IsInside(root + "-hermana" + Path.DirectorySeparatorChar + "a.json", root));
        Assert.False(SafePaths.IsInside(Path.Combine(root, "..", "a.json"), root));
    }

    // «y se rechazan entradas repetidas sin distinguir mayúsculas.»
    [Fact]
    public void Las_entradas_repetidas_se_rechazan_sin_distinguir_mayusculas()
    {
        string archive = Samples.Zip(_folder.Combine("dup.paquete"),
            ("paquete.json", Samples.Manifest()), ("transiciones/deslizar.json", "{}"), ("TRANSICIONES\\DESLIZAR.JSON", "{}"));

        var ex = Assert.Throws<PackageInstallException>(() => _installer.Install(archive));

        Assert.Contains("repite", ex.Message, StringComparison.Ordinal);
        Assert.Empty(PackageFolders());
    }

    // «20 000 entradas, 8 GB descomprimidos (los módulos traen modelos grandes), una tasa de compresión de 200 a 1 […]
    // y espacio libre en el disco para todo lo descomprimido más 256 MB.»
    [Fact]
    public void Los_limites_por_omision_son_los_del_documento()
    {
        Assert.Equal(20_000, InstallLimits.Default.MaxEntries);
        Assert.Equal(8L * 1024 * 1024 * 1024, InstallLimits.Default.MaxTotalBytes);
        Assert.Equal(200, InstallLimits.Default.MaxCompressionRatio);
        Assert.Equal(256L * 1024 * 1024, InstallLimits.Default.FreeSpaceReserve);
    }

    // «Hay límites contra zips maliciosos, que se comprueban antes de escribir nada: […] una tasa de compresión de 200 a
    // 1 en cada entrada de más de 1 MB»
    [Fact]
    public void La_tasa_de_cada_entrada_se_comprueba_antes_de_escribir_nada()
    {
        // «a» y luego «a/b»: escribir la segunda falla (ya hay un archivo donde iría la carpeta). Si la tasa de
        // «bomba.bin» se mirara recién al llegar a ella, el error sería el de la escritura.
        string path = _folder.Combine("bomba-tardia.paquete");
        using (var stream = new FileStream(path, FileMode.Create))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            WriteEntry(zip, "paquete.json", System.Text.Encoding.UTF8.GetBytes(Samples.Manifest()));
            WriteEntry(zip, "transiciones/deslizar.json", "{}"u8.ToArray());
            WriteEntry(zip, "a", "x"u8.ToArray());
            WriteEntry(zip, "a/b", "x"u8.ToArray());
            WriteEntry(zip, "bomba.bin", new byte[2 << 20], CompressionLevel.SmallestSize);
        }

        var ex = Assert.Throws<PackageInstallException>(() => _installer.Install(path));

        Assert.Contains("«bomba.bin» está comprimido de forma sospechosa", ex.Message, StringComparison.Ordinal);
        Assert.Empty(PackageFolders());
    }

    // «[la tasa de compresión] en cada entrada de más de 1 MB»: una entrada de 1 MB justo, aunque esté muy comprimida, pasa.
    [Fact]
    public void La_tasa_de_cada_entrada_solo_cuenta_si_pasa_de_1_mb()
    {
        string path = _folder.Combine("chica.paquete");
        using (var stream = new FileStream(path, FileMode.Create))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            WriteEntry(zip, "paquete.json", System.Text.Encoding.UTF8.GetBytes(Samples.Manifest()));
            WriteEntry(zip, "transiciones/deslizar.json", "{}"u8.ToArray());
            WriteEntry(zip, "ceros.bin", new byte[1 << 20], CompressionLevel.SmallestSize);
        }

        Assert.Equal(InstallOutcome.Installed, _installer.Install(path).Outcome);
    }

    // «y en el paquete entero si pasa de 16 MB (muchas entradas chicas muy comprimidas esquivan el límite de cada una)»
    [Fact]
    public void La_tasa_del_paquete_entero_solo_cuenta_si_pasa_de_16_mb()
    {
        string Build(string name, int entries)
        {
            string path = _folder.Combine(name);
            using var stream = new FileStream(path, FileMode.Create);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
            WriteEntry(zip, "paquete.json", System.Text.Encoding.UTF8.GetBytes(Samples.Manifest()));
            WriteEntry(zip, "transiciones/deslizar.json", "{}"u8.ToArray());
            for (int i = 0; i < entries; i++)
            {
                WriteEntry(zip, $"relleno/{i}.bin", new byte[1 << 20], CompressionLevel.SmallestSize);
            }

            return path;
        }

        Assert.Equal(InstallOutcome.Installed, _installer.Install(Build("15.paquete", 15)).Outcome);
        _installer.Uninstall("rodaje-transiciones");

        var ex = Assert.Throws<PackageInstallException>(() => _installer.Install(Build("17.paquete", 17)));
        Assert.Contains("El paquete está comprimido de forma sospechosa", ex.Message, StringComparison.Ordinal);
        Assert.Empty(PackageFolders());
    }

    // «Durante la copia, una entrada que trae más bytes de los que declara se corta.»
    [Fact]
    public void Una_entrada_que_trae_mas_bytes_de_los_que_declara_se_corta()
    {
        string path = _folder.Combine("miente.paquete");
        byte[] content = new byte[64 * 1024];
        new Random(358).NextBytes(content);
        using (var stream = new FileStream(path, FileMode.Create))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            WriteEntry(zip, "paquete.json", System.Text.Encoding.UTF8.GetBytes(Samples.Manifest()));
            WriteEntry(zip, "transiciones/deslizar.json", "{}"u8.ToArray());
            WriteEntry(zip, "grande.bin", content, CompressionLevel.NoCompression);
        }

        // Declara 100 bytes en la cabecera local y en el directorio central.
        byte[] bytes = File.ReadAllBytes(path);
        int patched = 0;
        for (int i = 0; i + 46 < bytes.Length; i++)
        {
            uint signature = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i));
            int sizeOffset = signature switch { 0x04034b50 => 22, 0x02014b50 => 24, _ => -1 };
            if (sizeOffset > 0 && BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i + sizeOffset)) == content.Length)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i + sizeOffset), 100);
                patched++;
            }
        }

        Assert.Equal(2, patched);
        File.WriteAllBytes(path, bytes);

        Assert.Throws<PackageInstallException>(() => _installer.Install(path));
        Assert.Empty(PackageFolders());
    }

    // «Se lee y valida `paquete.json` con sus archivos»
    [Fact]
    public void El_manifiesto_se_valida_con_sus_archivos()
    {
        string archive = Samples.Zip(_folder.Combine("sin-archivo.paquete"), ("paquete.json", Samples.Manifest()));

        var ex = Assert.Throws<PackageInstallException>(() => _installer.Install(archive));

        Assert.Contains("no es válido", ex.Message, StringComparison.Ordinal);
        Assert.Empty(PackageFolders());
    }

    // «y se comprueba que el paquete funcione en este programa.»
    [Fact]
    public void Se_comprueba_que_funcione_en_este_programa()
    {
        string archive = Samples.ValidPackage(_folder.Combine("otro.paquete"), apps: "[\"editor-audio\"]");

        Assert.Throws<PackageInstallException>(() => _installer.Install(archive));
        Assert.Equal(InstallOutcome.Installed, new PackageInstaller(_locations, "editor-audio").Install(archive).Outcome);
    }

    // «Si ya hay una versión instalada, se compara: una más nueva actualiza, la misma reinstala y una más vieja se rechaza
    // salvo que se pida (`allowDowngrade`).»
    [Fact]
    public void Se_compara_con_la_version_instalada()
    {
        string Package(string version) => Samples.ValidPackage(_folder.Combine($"v{version}.paquete"), version: version);
        _installer.Install(Package("1.0.0"));

        Assert.Equal(InstallOutcome.Updated, _installer.Install(Package("1.2.0")).Outcome);
        Assert.Equal(InstallOutcome.Reinstalled, _installer.Install(Package("1.2.0")).Outcome);
        Assert.Throws<PackageInstallException>(() => _installer.Install(Package("1.1.0")));
        Assert.Equal("1.2.0", PackageLibrary.Scan(_locations, "rodaje").Find("rodaje-transiciones")!.Manifest.Version.ToString());
        InstallResult back = _installer.Install(Package("1.1.0"), allowDowngrade: true);
        Assert.Equal(InstallOutcome.Downgraded, back.Outcome);
        Assert.Equal(PackageVersion.Parse("1.2.0"), back.PreviousVersion);
    }

    // «La carpeta anterior se aparta como `.anterior-<aleatorio>-<id>`, la nueva entra con otro cambio de nombre […] y la
    // vieja se borra después»
    [Fact]
    public void Al_actualizar_la_anterior_no_queda()
    {
        _installer.Install(Samples.ValidPackage(_folder.Combine("v1.paquete"), version: "1.0.0"));
        string marker = Path.Combine(_locations.Packages, "rodaje-transiciones", "solo-en-la-vieja.txt");
        File.WriteAllText(marker, "x");

        _installer.Install(Samples.ValidPackage(_folder.Combine("v2.paquete"), version: "2.0.0"));

        Assert.Equal(["rodaje-transiciones"], PackageFolders());
        Assert.False(File.Exists(marker));
    }

    // «Desinstalar aparta la carpeta como `.desinstalando-<aleatorio>` y la borra.»
    [Fact]
    public void Desinstalar_borra_la_carpeta_sin_dejar_restos()
    {
        _installer.Install(Samples.ValidPackage(_folder.Combine("t.paquete")));

        _installer.Uninstall("rodaje-transiciones");

        Assert.Empty(PackageFolders());
        Assert.Equal(0, _installer.CleanUpLeftovers());
    }

    // «Solo se desinstala lo instalado por la persona: lo incluido con el programa y lo propio no.»
    [Fact]
    public void Lo_incluido_con_el_programa_y_lo_propio_no_se_desinstalan()
    {
        string bundled = _folder.Combine("incluidos");
        string bundledPackage = Path.Combine(bundled, "rodaje-titulos");
        Directory.CreateDirectory(bundledPackage);
        File.WriteAllText(Path.Combine(bundledPackage, "paquete.json"), Samples.Manifest(id: "rodaje-titulos", items: "[]"));
        Directory.CreateDirectory(_locations.Own);
        File.WriteAllText(Path.Combine(_locations.Own, "mio.json"), "{}");
        var installer = new PackageInstaller(PackageLocations.Under(_folder.Combine("complementos"), [bundled]), "rodaje");

        Assert.Throws<PackageInstallException>(() => installer.Uninstall("rodaje-titulos"));
        Assert.Throws<PackageInstallException>(() => installer.Uninstall(PackageLibrary.OwnPackageId));
        Assert.Throws<PackageInstallException>(() => installer.Uninstall("propios"));
        Assert.True(File.Exists(Path.Combine(bundledPackage, "paquete.json")));
        Assert.True(File.Exists(Path.Combine(_locations.Own, "mio.json")));
    }

    // «`.instalando-*` y `.desinstalando-*` se borran»
    [Fact]
    public void La_limpieza_borra_instalando_y_desinstalando()
    {
        Directory.CreateDirectory(Path.Combine(_locations.Packages, ".instalando-0123456789ab", "a"));
        Directory.CreateDirectory(Path.Combine(_locations.Modules, ".instalando-0123456789ab"));
        Directory.CreateDirectory(Path.Combine(_locations.Packages, ".desinstalando-0123456789ab"));
        Directory.CreateDirectory(Path.Combine(_locations.Packages, "rodaje-titulos"));

        Assert.Equal(3, _installer.CleanUpLeftovers());
        Assert.Equal(["rodaje-titulos"], PackageFolders());
        Assert.Empty(Directory.GetDirectories(_locations.Modules));
    }

    // «una `.anterior-*` se borra si la versión nueva quedó completa (manifiesto válido con su id y todos sus archivos) y,
    // si no, vuelve a su sitio y la nueva a medias se descarta.»
    [Fact]
    public void La_anterior_se_borra_si_la_nueva_esta_completa_y_si_no_vuelve()
    {
        string target = _installer.Install(Samples.ValidPackage(_folder.Combine("v1.paquete"), version: "1.0.0")).Package.Directory;
        string aside = Path.Combine(_locations.Packages, ".anterior-0123456789ab-rodaje-transiciones");
        Directory.Move(target, aside);
        Directory.CreateDirectory(target);
        // Manifiesto válido pero falta el archivo que nombra: no está completa.
        File.WriteAllText(Path.Combine(target, "paquete.json"), Samples.Manifest(version: "2.0.0"));

        Assert.Equal(1, _installer.CleanUpLeftovers());
        Assert.Equal(["rodaje-transiciones"], PackageFolders());
        Assert.Equal("1.0.0", ManifestReader.ReadFile(Path.Combine(target, "paquete.json")).Manifest!.Version.ToString());

        Directory.CreateDirectory(Path.Combine(aside, "transiciones"));
        File.WriteAllText(Path.Combine(aside, "paquete.json"), Samples.Manifest(version: "0.5.0"));
        File.WriteAllText(Path.Combine(aside, "transiciones", "deslizar.json"), "{}");

        Assert.Equal(1, _installer.CleanUpLeftovers());
        Assert.Equal(["rodaje-transiciones"], PackageFolders());
        Assert.Equal("1.0.0", ManifestReader.ReadFile(Path.Combine(target, "paquete.json")).Manifest!.Version.ToString());
    }

    // «Además, instalar, desinstalar y limpiar toman `complementos\.operacion.lock` […] El archivo se queda en la carpeta y
    // no molesta.»
    [Fact]
    public void El_bloqueo_es_complementos_operacion_lock_y_se_queda()
    {
        Assert.Equal(_folder.Combine("complementos", ".operacion.lock"), _installer.OperationLockPath);

        _installer.Install(Samples.ValidPackage(_folder.Combine("t.paquete")));
        Assert.True(File.Exists(_installer.OperationLockPath));
        _installer.Uninstall("rodaje-transiciones");
        _installer.CleanUpLeftovers();

        Assert.True(File.Exists(_installer.OperationLockPath));
        Assert.Equal(InstallOutcome.Installed, _installer.Install(Samples.ValidPackage(_folder.Combine("t2.paquete"))).Outcome);
    }

    // «(abierto sin compartir mientras dura la operación; si otro proceso lo tiene, se espera hasta 10 s y luego se avisa)»
    [Fact]
    public void Con_el_bloqueo_tomado_se_espera_hasta_10_s_y_luego_se_avisa()
    {
        Assert.Equal(TimeSpan.FromSeconds(10), _installer.BusyTimeout);

        var installer = new PackageInstaller(_locations, "rodaje") { BusyTimeout = TimeSpan.FromMilliseconds(300) };
        string archive = Samples.ValidPackage(_folder.Combine("t.paquete"));
        installer.Install(archive);
        bool held = false;
        installer.Install(archive, beforeCommit: _ =>
        {
            try
            {
                using var other = new FileStream(installer.OperationLockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            }
            catch (IOException)
            {
                held = true;
            }
        });
        Assert.True(held);

        using (new FileStream(installer.OperationLockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var ex = Assert.Throws<PackageInstallException>(() => installer.Uninstall("rodaje-transiciones"));

            // Fuera de Windows el bloqueo no da violación de uso compartido y se avisa sin esperar.
            if (OperatingSystem.IsWindows())
            {
                Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(250), $"Esperó {watch.Elapsed}.");
                Assert.Contains("prueba de nuevo", ex.Message, StringComparison.Ordinal);
            }
        }

        Assert.Equal(["rodaje-transiciones"], PackageFolders());
    }
}
