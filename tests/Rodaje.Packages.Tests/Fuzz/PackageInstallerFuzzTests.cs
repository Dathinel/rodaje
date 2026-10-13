using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Rodaje.Packages.Content;
using Rodaje.Tests.Fuzz;
using Xunit.Abstractions;

namespace Rodaje.Packages.Tests.Fuzz;

/// <summary>
/// Pruebas hostiles del instalador: un <c>.paquete</c> dañado o malicioso se instala bien o lanza
/// <see cref="PackageInstallException"/> (con causa de E/S, zip o JSON, o ninguna); nunca otra excepción, nunca escribe
/// fuera de su carpeta y un fallo no deja nada. Mutaciones deterministas (semilla fija) sobre un paquete válido
/// generado aquí; el contenido instalado se lee con los lectores, que solo pueden lanzar <see cref="PackageContentException"/>.
/// </summary>
[Collection(TimingGroup.Name)]
public class PackageInstallerFuzzTests(ITestOutputHelper output)
{
    private const int Seed = 20261011;
    private const int Cases = 5200;
    private const double MinimumPastZipShare = 0.25;

    // Marca que MutateManifestJson/HugeManifest/MutateContent ponen en un texto y Escapes() cambia por un escape JSON
    // de sustituto suelto: JsonNode no puede escribir un sustituto suelto, así que se inserta ya como texto.
    private const string EscapeMarker = "@@S@@";

    // Cada instalación vuelca a disco: los 5200 casos tardaron 146 s en el runner de Windows de la CI (antivirus de por
    // medio) y de 13 a 26 s en Linux. Sin RODAJE_PRUEBAS_LARGAS corre un subconjunto determinista (todos los nombres
    // peligrosos y 1 de cada 10 del resto); con ella, los 5200 casos en cualquier sistema. El tiempo se registra, no se
    // exige.
    private static readonly bool Subset = !FuzzRunner.Long;

    private static readonly string Cube = "TITLE \"t\"\nLUT_3D_SIZE 2\n0 0 0\n1 0 0\n0 1 0\n1 1 0\n0 0 1\n1 0 1\n0 1 1\n1 1 1\n";

    private static readonly string Filter = ("""
        { "format": "rodaje.filter", "version": 1, "id": "cine", "name": "Cine",
          "effects": [ { "type": "rodaje.color", "parameters": { "lutIntensity": 1 }, "options": { "lut": "./lut-cine" } },
                       { "type": "rodaje.vignette", "parameters": { "amount": 0.25 } } ] }
        """).ReplaceLineEndings("\n");

    private static readonly string Effect = ("""
        { "format": "rodaje.effect", "version": 1, "name": "Entrada con zoom", "anchor": "start", "durationSeconds": 0.5,
          "effects": [],
          "curves": [ { "effect": "rodaje.transform", "parameter": "scale",
                        "keys": [ { "t": 0, "value": 1.3, "interp": "easeOut" }, { "t": 0.5, "value": 1 } ] } ] }
        """).ReplaceLineEndings("\n");

    private static readonly string Transition = ("""
        { "format": "rodaje.preset", "version": 2, "id": "empujar", "name": "Empujar", "kind": "transition", "durationSeconds": 0.5,
          "mix": "rodaje.wipe", "mixParameters": { "angle": 180, "softness": 0 },
          "reference": { "width": 1920, "height": 1080 },
          "outgoing": { "x": [ { "t": 0, "value": 0 }, { "t": 0.5, "value": -1920 } ] },
          "incoming": { "x": [ { "t": 0, "value": 1920 }, { "t": 0.5, "value": 0 } ] } }
        """).ReplaceLineEndings("\n");

    private static readonly string Items = ("""
        [ { "id": "lut-cine", "kind": "lut", "name": "Cine", "file": "luts/cine.cube" },
          { "id": "cine", "kind": "filter", "name": "Cine", "file": "filtros/cine.json" },
          { "id": "zoom", "kind": "effect", "name": "Zoom", "file": "efectos/zoom.json" },
          { "id": "empujar", "kind": "transition", "name": "Empujar", "file": "transiciones/empujar.json" } ]
        """).ReplaceLineEndings("\n");

    // Nombres que SafePaths tiene que rechazar: el instalador debe fallar con «ruta no permitida» o «fuera de su carpeta».
    private static readonly string[] MustReject =
    [
        "../x", "..\\x", "/abs/x", "C:\\x", "C:x", "\\\\srv\\share\\x", "con", "nul.txt", "CON.json", "COM1", "lpt9.x", "a/../../b", "a/./b",
        "a\u0001b", "x\u202Efdp.json", "x\u200Fy", "trailing.", "trailing ", "a:b", "a*b", "a?b", "a|b", "./", "a//b", "..", ".",
        "a\0b", new string('n', 5000), string.Join('/', Enumerable.Repeat("d", 400)) + "/f", "..\\..\\..\\etc\\passwd", "aux/x", "PRN", "nul", "CONOUT$",
    ];

    // Entradas que repiten un archivo del paquete (también con otra caja o como carpeta): «repite el archivo».
    private static readonly string[] Duplicates = ["PAQUETE.JSON", "paquete.json/", "luts/cine.cube/", "Luts/Cine.Cube"];

    // Nombres raros pero legítimos: pueden instalar; solo se vigila que no escapen ni dejen rastro.
    private static readonly string[] Harmless = ["~", "%00", "ñandú/ß.json", ".instalando-abc/x", "propios/x", "a b/c", "x.y.z"];

    // Centinelas: nombres que, de instalarse mal, dejarían un rastro. Los hay absolutos (dentro de la carpeta temporal de
    // la prueba), relativos con «..», UNC y «C:x» (unidad sin barra, relativa a la unidad actual en Windows).
    private const string UncSentinel = "\\\\srv\\share\\centinela-unc\\x";
    private const string DriveSentinel = "C:centinela-unidad/x";

    private static string[] Sentinels(string tempPath) =>
    [
        Path.Combine(tempPath, "centinela-abs", "x"),
        "../../../../centinela-subir/x",
        "..\\..\\..\\..\\centinela-subir2\\x",
        "../../../centinela-raiz/x",
        UncSentinel,
        DriveSentinel,
    ];

    /// <summary>
    /// Dónde no debe aparecer rastro de un centinela. Los relativos se miran contra el directorio actual y contra la
    /// carpeta temporal (no contra sí mismos, que dependería del directorio desde el que corre la prueba).
    /// </summary>
    private static IEnumerable<string> SentinelProbes(string tempPath)
    {
        string current = Directory.GetCurrentDirectory();
        foreach (string name in Sentinels(tempPath))
        {
            if (Path.IsPathRooted(name) && !name.StartsWith('\\') && !name.StartsWith("C:", StringComparison.Ordinal))
            {
                yield return name;
                continue;
            }

            yield return Path.Combine(current, name);
            yield return Path.Combine(tempPath, name);
        }
    }

    private enum Expect
    {
        Anything,
        PathRejected,
        Duplicate,
    }

    private sealed record FuzzCase(byte[] Data, Expect Expect = Expect.Anything, string? Name = null);

    [Fact]
    public async Task HostilePackages_InstallOrFailInControlledWayAndLeaveNothing()
    {
        var stopwatch = Stopwatch.StartNew();
        var random = new Random(Seed);
        using var temp = new TempFolder();
        List<(string Name, byte[] Data)> baseEntries = BaseEntries();
        string[] dangerous = [.. MustReject, .. Sentinels(temp.Path)];

        // Los casos salen todos de la misma semilla, en orden; luego se reparten entre trabajadores con su propia
        // carpeta (cada instalación vuelca a disco y, con un solo trabajador, 5000 casos pasan de 30 s).
        var all = new FuzzCase[Cases];
        for (int i = 0; i < Cases; i++)
        {
            all[i] = Mutate(random, baseEntries, i, dangerous);
        }

        int[] selected = SelectCases(all);
        AssertEveryDangerousNameSelected(all, selected, dangerous);
        const int Workers = 4;
        var stages = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
        // Hilos propios: los trabajadores bloquean esperando la carga de la LUT y no deben quitarle hilos al grupo que la hace.
        Task[] tasks = Enumerable.Range(0, Workers).Select(w => Task.Factory.StartNew(() =>
        {
            string root = temp.Combine("raiz" + w);
            string data = Path.Combine(root, "datos");
            string archive = Path.Combine(temp.Path, "caso" + w + ".paquete");
            var installer = new PackageInstaller(PackageLocations.Under(data), "rodaje") { BusyTimeout = TimeSpan.FromSeconds(2) };

            // Estado vacío de referencia: la primera instalación crea las carpetas y el archivo de operación.
            File.WriteAllBytes(archive, Build(baseEntries));
            installer.Install(archive);
            installer.Uninstall("rodaje-fuzz");
            HashSet<string> empty = Snapshot(root, archive);
            for (int n = w; n < selected.Length; n += Workers)
            {
                int i = selected[n];
                FuzzCase fuzz = all[i];
                File.WriteAllBytes(archive, fuzz.Data);
                string stage;
                string? failure = null;
                try
                {
                    InstallResult result = installer.Install(archive, allowDowngrade: true);
                    CheckInstalledInside(result, Path.GetFullPath(data));
                    stage = ReadContent(result) ? "instalado, contenido válido" : "instalado, contenido rechazado";
                    installer.Uninstall(result.Package.Id);
                }
                catch (PackageInstallException ex)
                {
                    failure = ex.Message;
                    stage = ClassifyFailure(ex, i);
                }

                CheckExpectation(fuzz, failure, i);
                stages.AddOrUpdate(stage, 1, (_, count) => count + 1);
                HashSet<string> now = Snapshot(root, archive);
                if (!now.SetEquals(empty))
                {
                    string extra = string.Join(", ", now.Except(empty).Concat(empty.Except(now).Select(p => "falta " + p)).Take(5));
                    Assert.Fail($"El caso {i} ({stage}) dejó cambios fuera del estado vacío: {extra}");
                }
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
        await Task.WhenAll(tasks);

        // Escrituras fuera de las carpetas de los trabajadores: los centinelas (rutas absolutas y con «..») y cualquier otra cosa.
        string[] stray = [.. Directory.EnumerateFileSystemEntries(temp.Path).Select(e => Path.GetFileName(e)!)
            .Where(n => !n.StartsWith("raiz", StringComparison.Ordinal) && !n.StartsWith("caso", StringComparison.Ordinal))];
        Assert.Empty(stray);
        Assert.All(SentinelProbes(temp.Path), n => Assert.False(File.Exists(n) || Directory.Exists(n), $"Apareció el centinela {n}"));

        stopwatch.Stop();
        int Stage(string name) => stages.GetValueOrDefault(name);
        int executed = selected.Length;
        int pastZip = executed - Stage("zip");
        output.WriteLine($"{executed} de {Cases} casos en {stopwatch.Elapsed.TotalSeconds:0.0} s (no se exige tiempo): " +
                         string.Join(", ", stages.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key} {kv.Value}")) + ".");
        Assert.True(Subset || executed >= 5000, "Con pruebas largas corren al menos 5000 casos.");
        Assert.True(pastZip >= executed * MinimumPastZipShare, $"Solo {pastZip} de {executed} casos pasan del zip.");
        foreach (string needed in new[] { "manifiesto", "ruta", "duplicado", "instalado, contenido válido", "instalado, contenido rechazado" })
        {
            Assert.True(Stage(needed) > 0, $"Ningún caso llega a la etapa «{needed}».");
        }
    }

    /// <summary>Sin pruebas largas, todos los nombres peligrosos (el primer caso de cada uno) y 1 de cada 10 del resto; con ellas, todos.</summary>
    private static int[] SelectCases(FuzzCase[] all)
    {
        if (!Subset)
        {
            return [.. Enumerable.Range(0, all.Length)];
        }

        var firstOfName = new HashSet<string>(StringComparer.Ordinal);
        var chosen = new List<int>();
        for (int i = 0; i < all.Length; i++)
        {
            bool first = all[i].Name is { } name && firstOfName.Add(name);
            if (first || i % 10 == 0)
            {
                chosen.Add(i);
            }
        }

        return [.. chosen];
    }

    /// <summary>
    /// Cada nombre peligroso tiene que aparecer en los casos generados y estar entre los que corren (sin pruebas largas corre un
    /// subconjunto), o la prueba no vigilaría ese nombre.
    /// </summary>
    private static void AssertEveryDangerousNameSelected(FuzzCase[] all, int[] selected, string[] dangerous)
    {
        var generated = new HashSet<string>(all.Where(c => c.Expect == Expect.PathRejected).Select(c => c.Name!), StringComparer.Ordinal);
        var running = new HashSet<string>(selected.Select(i => all[i]).Where(c => c.Expect == Expect.PathRejected).Select(c => c.Name!), StringComparer.Ordinal);
        string[] notGenerated = [.. dangerous.Where(n => !generated.Contains(n)).Select(Show)];
        string[] notRunning = [.. dangerous.Where(n => generated.Contains(n) && !running.Contains(n)).Select(Show)];
        Assert.True(notGenerated.Length == 0, "Ningún caso usa estos nombres peligrosos: " + string.Join(", ", notGenerated));
        Assert.True(notRunning.Length == 0, "El subconjunto no cubre estos nombres peligrosos: " + string.Join(", ", notRunning));
    }

    /// <summary>Los nombres peligrosos tienen que fallar por su ruta; las entradas repetidas, por repetirse.</summary>
    private static void CheckExpectation(FuzzCase fuzz, string? failure, int caseIndex)
    {
        switch (fuzz.Expect)
        {
            case Expect.PathRejected:
                Assert.True(failure is not null && (failure.Contains("ruta no permitida", StringComparison.Ordinal) || failure.Contains("fuera de su carpeta", StringComparison.Ordinal)),
                    $"Caso {caseIndex}: «{Show(fuzz.Name)}» tenía que rechazarse como ruta no permitida o fuera de su carpeta; resultado: {failure ?? "se instaló"}");
                break;
            case Expect.Duplicate:
                Assert.True(failure is not null && failure.Contains("repite el archivo", StringComparison.Ordinal),
                    $"Caso {caseIndex}: «{Show(fuzz.Name)}» tenía que rechazarse por repetido; resultado: {failure ?? "se instaló"}");
                break;
        }
    }

    private static string Show(string? name) => name is null ? "" : name.Length > 60 ? name[..60] + "…" : name;

    [Fact]
    public void PresetReader_InvalidUtf8InString_ThrowsPackageContentException()
    {
        byte[] json = [.. "{ \"format\": \"rodaje.filter\", \"version\": 1, \"name\": \"a"u8, 0xCE, .. "b\", \"effects\": [] }"u8];
        Assert.Throws<PackageContentException>(() => ContentReaders.ReadFilter(new MemoryStream(json)));
    }

    [Fact]
    public void Install_InvalidRepositoryHost_ThrowsPackageInstallException()
    {
        using var temp = new TempFolder();
        string archive = Samples.Zip(temp.Combine("p.paquete"),
            ("paquete.json", Samples.Manifest(items: "[]").Replace("https://github.com/Dathinel/rodaje-transiciones", "https://a\uFFFDb.com/x", StringComparison.Ordinal)));
        var installer = new PackageInstaller(PackageLocations.Under(temp.Combine("datos")), "rodaje");
        Assert.Throws<PackageInstallException>(() => installer.Install(archive));
    }

    [Fact]
    public void Install_LoneSurrogateEscapeInManifest_ThrowsPackageInstallException()
    {
        using var temp = new TempFolder();
        string archive = Samples.Zip(temp.Combine("p.paquete"),
            ("paquete.json", Samples.Manifest(items: "[]").Replace("Transiciones de prueba", "a\\ud800b", StringComparison.Ordinal)));
        var installer = new PackageInstaller(PackageLocations.Under(temp.Combine("datos")), "rodaje");
        Assert.Throws<PackageInstallException>(() => installer.Install(archive));
    }

    [Fact]
    public void Scan_LoneSurrogateEscapeInManifest_ReportsAProblemInsteadOfThrowing()
    {
        using var temp = new TempFolder();
        PackageLocations locations = PackageLocations.Under(temp.Combine("datos"));
        string folder = Path.Combine(locations.Packages, "rodaje-transiciones");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "paquete.json"), Samples.Manifest(items: "[]").Replace("Transiciones de prueba", "a\\ud800b", StringComparison.Ordinal));
        PackageScan scan = PackageLibrary.Scan(locations, "rodaje");
        Assert.Contains(scan.Problems, p => p.Directory == folder);
    }

    [Fact]
    public void PresetReader_LoneSurrogateEscape_ThrowsPackageContentException()
    {
        byte[] json = Encoding.UTF8.GetBytes("{ \"format\": \"rodaje.filter\", \"version\": 1, \"name\": \"a\\ud800b\", \"effects\": [] }");
        Assert.Throws<PackageContentException>(() => ContentReaders.ReadFilter(new MemoryStream(json)));
    }

    [Fact]
    public void HostileContentFiles_OnlyThrowPackageContentException()
    {
        var random = new Random(Seed + 1);
        (string Name, string Text, Action<Stream> Read)[] seeds =
        [
            ("cube", Cube, s => ContentReaders.ReadCube(s)),
            ("filter", Filter, s => ContentReaders.ReadFilter(s)),
            ("effect", Effect, s => ContentReaders.ReadEffect(s)),
            ("transition", Transition, s => ContentReaders.ReadTransition(s)),
        ];
        int ok = 0;
        int rejected = 0;
        for (int i = 0; i < 4000; i++)
        {
            (string name, string text, Action<Stream> read) = seeds[i % seeds.Length];
            byte[] data = MutateContent(random, text, name == "cube");
            try
            {
                read(new MemoryStream(data));
                ok++;
            }
            catch (PackageContentException ex)
            {
                CheckContentException(ex);
                rejected++;
            }
        }

        output.WriteLine($"contenido: {ok} leídos, {rejected} rechazados");
        Assert.True(ok > 0 && rejected > 0);
    }

    // --- Instalación ---

    private static void CheckInstalledInside(InstallResult result, string dataRoot)
    {
        string dir = Path.GetFullPath(result.Package.Directory);
        Assert.StartsWith(dataRoot + Path.DirectorySeparatorChar, dir, StringComparison.Ordinal);
        foreach (string file in Directory.EnumerateFileSystemEntries(dir, "*", SearchOption.AllDirectories))
        {
            Assert.StartsWith(dir + Path.DirectorySeparatorChar, Path.GetFullPath(file), StringComparison.Ordinal);
        }
    }

    /// <summary>Mensaje en español y causa (si hay) solo de E/S, zip o JSON; si no, la prueba falla.</summary>
    private static void CheckContentException(PackageContentException ex)
    {
        Assert.False(string.IsNullOrWhiteSpace(ex.Message), "PackageContentException sin mensaje.");
        Assert.True(ex.InnerException is null or JsonException, $"Causa inesperada {ex.InnerException?.GetType().Name}: {ex.InnerException?.Message} (mensaje: {ex.Message})");
    }

    /// <summary>
    /// Etapa de un fallo de instalación; falla la prueba si no tiene mensaje, si la causa no es de E/S o zip o si el mensaje
    /// no es de ninguna etapa conocida (un envoltorio genérico).
    /// </summary>
    private static string ClassifyFailure(PackageInstallException ex, int caseIndex)
    {
        Assert.False(string.IsNullOrWhiteSpace(ex.Message), $"Caso {caseIndex}: mensaje vacío.");
        Exception? inner = ex.InnerException;
        if (inner is InvalidDataException)
        {
            return "zip";
        }

        if (inner is not null && inner is not (IOException or UnauthorizedAccessException))
        {
            Assert.Fail($"Caso {caseIndex}: causa inesperada {inner.GetType().Name}: {inner.Message} (mensaje: {ex.Message})");
        }

        string m = ex.Message;
        if (m.Contains("ruta no permitida", StringComparison.Ordinal) || m.Contains("fuera de su carpeta", StringComparison.Ordinal))
        {
            return "ruta";
        }

        if (m.Contains("repite el archivo", StringComparison.Ordinal))
        {
            return "duplicado";
        }

        if (m.Contains("comprimido de forma sospechosa", StringComparison.Ordinal) || m.Contains("más grande de lo que declara", StringComparison.Ordinal)
            || m.Contains("ocupa más de", StringComparison.Ordinal) || m.Contains("archivos.", StringComparison.Ordinal))
        {
            return "tamaño o compresión";
        }

        if (m.StartsWith("El paquete no es válido", StringComparison.Ordinal) || m.Contains("funciona en", StringComparison.Ordinal)
            || m.Contains("está reservado", StringComparison.Ordinal) || m.Contains("distinto tipo", StringComparison.Ordinal))
        {
            return "manifiesto";
        }

        if (inner is not null)
        {
            return "E/S";
        }

        Assert.Fail($"Caso {caseIndex}: fallo sin etapa conocida ni causa: {m}");
        return "";
    }

    /// <summary>Lee los cuatro archivos de contenido instalados; verdadero si todos son válidos.</summary>
    private static bool ReadContent(InstallResult result)
    {
        InstalledPackage package = result.Package;
        bool allOk = true;
        foreach (PackageItem item in package.Manifest.Items)
        {
            if (item.File is null)
            {
                continue;
            }

            string path = Path.Combine(package.Directory, item.File);
            try
            {
                using FileStream stream = File.OpenRead(path);
                switch (item.Kind)
                {
                    case ItemKind.Lut:
                        ContentReaders.ReadCube(stream);
                        break;
                    case ItemKind.Filter:
                        ContentReaders.ReadFilter(stream);
                        break;
                    case ItemKind.Effect:
                        ContentReaders.ReadEffect(stream);
                        break;
                    case ItemKind.Transition:
                        ContentReaders.ReadTransition(stream);
                        break;
                }
            }
            catch (PackageContentException ex)
            {
                CheckContentException(ex);
                allOk = false;
            }
        }

        // El almacén tampoco debe lanzar, aunque el contenido esté dañado, y si no da el elemento dice por qué.
        using var store = new PackageContentStore(new PackageScan([package], []));
        string prefix = package.Id + "@" + package.Manifest.Version + "/";
        CheckProblem(store, prefix + "cine", store.FindFilter(prefix + "cine"));
        CheckProblem(store, prefix + "zoom", store.FindEffect(prefix + "zoom"));
        CheckProblem(store, prefix + "empujar", store.FindTransition(prefix + "empujar"));
        _ = store.TryGetLut(prefix + "lut-cine", out _);

        Assert.True(store.WhenLoaded().Wait(TimeSpan.FromSeconds(60)), "La carga de la LUT no terminó en 60 s.");
        if (!store.TryGetLut(prefix + "lut-cine", out _))
        {
            Assert.False(string.IsNullOrWhiteSpace(store.LutProblem(prefix + "lut-cine")), "La LUT no se leyó y no dice por qué.");
        }

        return allOk;
    }

    private static void CheckProblem(PackageContentStore store, string reference, object? found)
    {
        if (found is null)
        {
            Assert.False(string.IsNullOrWhiteSpace(store.PresetProblem(reference)), $"{reference} no se leyó y no dice por qué.");
        }
    }

    private static HashSet<string> Snapshot(string root, string archive)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (string entry in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
        {
            if (!string.Equals(entry, archive, StringComparison.Ordinal))
            {
                set.Add(Path.GetRelativePath(root, entry));
            }
        }

        return set;
    }

    // --- Construcción y mutaciones ---

    private static List<(string Name, byte[] Data)> BaseEntries() =>
    [
        ("paquete.json", Encoding.UTF8.GetBytes(Samples.Manifest(id: "rodaje-fuzz", items: Items))),
        ("luts/cine.cube", Encoding.UTF8.GetBytes(Cube)),
        ("filtros/cine.json", Encoding.UTF8.GetBytes(Filter)),
        ("efectos/zoom.json", Encoding.UTF8.GetBytes(Effect)),
        ("transiciones/empujar.json", Encoding.UTF8.GetBytes(Transition)),
    ];

    private static byte[] Build(IEnumerable<(string Name, byte[] Data)> entries, CompressionLevel level = CompressionLevel.Optimal)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string name, byte[] data) in entries)
            {
                ZipArchiveEntry entry = zip.CreateEntry(name, level);
                using Stream s = entry.Open();
                s.Write(data);
            }
        }

        return memory.ToArray();
    }

    private static FuzzCase Mutate(Random random, List<(string Name, byte[] Data)> baseEntries, int index, string[] dangerous)
    {
        // Un caso de cada 150 es una bomba de compresión; el resto, repartido entre las demás familias.
        if (index % 150 == 7)
        {
            return new FuzzCase(Bomb(random));
        }

        List<(string Name, byte[] Data)> entries = baseEntries.Select(e => (Name: e.Name, Data: (byte[])e.Data.Clone())).ToList();
        switch (random.Next(14))
        {
            case 0: // sin tocar: debe instalar
                return new FuzzCase(Build(entries));
            case 1: // entrada nueva con un nombre peligroso, repetido o raro pero legítimo
            {
                int pick = random.Next(10);
                if (pick < 6)
                {
                    string name = dangerous[random.Next(dangerous.Length)];
                    entries.Add((name, [1, 2, 3]));
                    return new FuzzCase(Build(entries), Expect.PathRejected, name);
                }

                if (pick < 8)
                {
                    string name = Duplicates[random.Next(Duplicates.Length)];
                    entries.Add((name, [1, 2, 3]));
                    return new FuzzCase(Build(entries), Expect.Duplicate, name);
                }

                entries.Add((Harmless[random.Next(Harmless.Length)], [1, 2, 3]));
                return new FuzzCase(Build(entries));
            }

            case 2: // se renombra una entrada existente con un nombre peligroso
            {
                string name = dangerous[random.Next(dangerous.Length)];
                int target = random.Next(entries.Count);
                entries[target] = (name, entries[target].Data);
                return new FuzzCase(Build(entries), Expect.PathRejected, name);
            }

            case 3: // entrada duplicada (misma o distinta caja)
            {
                (string name, byte[] data) = entries[random.Next(entries.Count)];
                string repeated = random.Next(2) == 0 ? name : name.ToUpperInvariant();
                entries.Add((repeated, data));
                return new FuzzCase(Build(entries), Expect.Duplicate, repeated);
            }

            case 4: // envuelto en una carpeta, a veces con restos
            {
                List<(string Name, byte[] Data)> wrapped = entries.Select(e => (Name: "carpeta-1.0/" + e.Name, e.Data)).ToList();
                if (random.Next(2) == 0)
                {
                    wrapped.Add(("otra/x", new byte[] { 1 }));
                }

                return new FuzzCase(Build(wrapped));
            }

            case 5: // manifiesto con tipos o valores cambiados
                entries[0] = (entries[0].Name, MutateManifestJson(random, entries[0].Data));
                return new FuzzCase(Build(entries));
            case 6: // manifiesto con bytes cambiados
                entries[0] = (entries[0].Name, MutateBytes(random, entries[0].Data));
                return new FuzzCase(Build(entries));
            case 7: // contenido dañado (bytes)
            case 8:
            {
                int target = 1 + random.Next(entries.Count - 1);
                entries[target] = (entries[target].Name, MutateBytes(random, entries[target].Data));
                return new FuzzCase(Build(entries));
            }

            case 9: // contenido dañado (estructura)
            {
                int target = 1 + random.Next(entries.Count - 1);
                entries[target] = (entries[target].Name, MutateContent(random, Encoding.UTF8.GetString(entries[target].Data), target == 1));
                return new FuzzCase(Build(entries));
            }

            case 10: // faltan archivos o el manifiesto
                entries.RemoveAt(random.Next(entries.Count));
                return new FuzzCase(Build(entries));
            case 11: // zip truncado o con bytes cambiados
            {
                byte[] zip = Build(entries);
                return new FuzzCase(random.Next(2) == 0 ? zip[..random.Next(zip.Length)] : MutateBytes(random, zip, 1 + random.Next(6)));
            }

            case 12: // directorio central falso o tamaños declarados falsos
                return new FuzzCase(FakeSizes(random, Build(entries)));
            default: // textos enormes, con marcas bidi o con escapes de sustituto en el manifiesto
                entries[0] = (entries[0].Name, HugeManifest(random, entries[0].Data));
                return new FuzzCase(Build(entries));
        }
    }

    /// <summary>Cambia cada <see cref="EscapeMarker"/> por un escape JSON de sustituto suelto, par inverso o U+0000.</summary>
    private static string Escapes(Random random, string json)
    {
        string[] escapes = ["\\ud800", "\\udc00", "\\u0000", "\\ud800\\ud800", "\\udc00\\ud800", "\\ud83d\\ude00"];
        while (json.Contains(EscapeMarker, StringComparison.Ordinal))
        {
            int at = json.IndexOf(EscapeMarker, StringComparison.Ordinal);
            json = string.Concat(json.AsSpan(0, at), escapes[random.Next(escapes.Length)], json.AsSpan(at + EscapeMarker.Length));
        }

        return json;
    }

    private static byte[] Bomb(Random random)
    {
        byte[] zeros = new byte[(2 + random.Next(4)) << 20];
        List<(string Name, byte[] Data)> entries = BaseEntries();
        entries.Add(("relleno/ceros.bin", zeros));
        return Build(entries);
    }

    private static byte[] FakeSizes(Random random, byte[] zip)
    {
        byte[] copy = (byte[])zip.Clone();
        for (int i = 0; i + 30 < copy.Length; i++)
        {
            bool central = copy[i] == 0x50 && copy[i + 1] == 0x4b && copy[i + 2] == 0x01 && copy[i + 3] == 0x02;
            bool local = copy[i] == 0x50 && copy[i + 1] == 0x4b && copy[i + 2] == 0x03 && copy[i + 3] == 0x04;
            if ((central || local) && random.Next(2) == 0)
            {
                int offset = i + (central ? 24 : 22);
                uint value = random.Next(3) switch { 0 => 0u, 1 => uint.MaxValue, _ => (uint)random.Next() };
                BitConverter.GetBytes(value).CopyTo(copy, offset);
            }
        }

        if (random.Next(3) == 0 && copy.Length > 22)
        {
            // Registro final: número de entradas y posición del directorio central.
            int end = copy.Length - 22;
            BitConverter.GetBytes((ushort)random.Next(0x10000)).CopyTo(copy, end + 10);
            BitConverter.GetBytes((uint)random.Next()).CopyTo(copy, end + 16);
        }

        return copy;
    }

    private static byte[] MutateManifestJson(Random random, byte[] manifest)
    {
        JsonNode root = JsonNode.Parse(manifest)!;
        var paths = new List<JsonObject>();
        Collect(root, paths);
        JsonObject target = paths[random.Next(paths.Count)];
        string[] keys = [.. target.Select(p => p.Key)];
        if (keys.Length == 0)
        {
            return manifest;
        }

        string key = keys[random.Next(keys.Length)];
        target[key] = random.Next(40) switch
        {
            0 => null,
            1 => 0,
            2 => -1,
            3 => 1e308,
            4 => long.MaxValue,
            5 => true,
            6 => new JsonArray(),
            7 => new JsonObject(),
            8 => "",
            9 => "..",
            10 => new JsonArray(1, "a", null),
            11 => "a" + EscapeMarker + "b",
            < 20 => "x\u202Ey",
            _ => "v",
        };
        return Encoding.UTF8.GetBytes(Escapes(random, root.ToJsonString()));
    }

    private static void Collect(JsonNode? node, List<JsonObject> into)
    {
        switch (node)
        {
            case JsonObject o:
                into.Add(o);
                foreach (KeyValuePair<string, JsonNode?> p in o)
                {
                    Collect(p.Value, into);
                }

                break;
            case JsonArray a:
                foreach (JsonNode? n in a)
                {
                    Collect(n, into);
                }

                break;
        }
    }

    private static byte[] HugeManifest(Random random, byte[] manifest)
    {
        JsonObject root = JsonNode.Parse(manifest)!.AsObject();
        string[] fields = ["name", "description", "author", "license", "repository", "id", "version"];
        string field = fields[random.Next(fields.Length)];
        root[field] = random.Next(12) switch
        {
            0 => new string('a', 1 << 20),
            1 => "Nombre\u202Eexe.json\u2066",
            2 => "Nombre " + EscapeMarker + " largo",
            < 8 => string.Concat(Enumerable.Repeat("\u0007\u200E", 300)),
            _ => new string('a', 300),
        };
        return Encoding.UTF8.GetBytes(Escapes(random, root.ToJsonString()));
    }

    private static byte[] MutateBytes(Random random, byte[] data, int edits = 0)
    {
        List<byte> list = [.. data];
        int count = edits > 0 ? edits : 1 + random.Next(4);
        for (int e = 0; e < count && list.Count > 0; e++)
        {
            int at = random.Next(list.Count);
            switch (random.Next(5))
            {
                case 0:
                    list[at] = (byte)random.Next(256);
                    break;
                case 1:
                    list.RemoveAt(at);
                    break;
                case 2:
                    list.Insert(at, (byte)random.Next(256));
                    break;
                case 3:
                    list.RemoveRange(at, Math.Min(list.Count - at, 1 + random.Next(16)));
                    break;
                default:
                    list[at] ^= (byte)(1 << random.Next(8));
                    break;
            }
        }

        return [.. list];
    }

    private static byte[] MutateContent(Random random, string text, bool isCube)
    {
        if (random.Next(3) == 0)
        {
            return MutateBytes(random, Encoding.UTF8.GetBytes(text));
        }

        if (isCube)
        {
            string[] lines = text.Split('\n');
            int i = random.Next(lines.Length);
            lines[i] = random.Next(8) switch
            {
                0 => "",
                1 => "NaN NaN NaN",
                2 => "1e999 0 0",
                3 => "0 0",
                4 => "LUT_3D_SIZE 99999",
                5 => "LUT_1D_SIZE 4",
                6 => "DOMAIN_MIN 1 1 1",
                _ => new string('9', 1 << 12),
            };
            return Encoding.UTF8.GetBytes(string.Join('\n', lines));
        }

        JsonNode root = JsonNode.Parse(text)!;
        var objects = new List<JsonObject>();
        Collect(root, objects);
        JsonObject target = objects[random.Next(objects.Count)];
        string[] keys = [.. target.Select(p => p.Key)];
        if (keys.Length == 0)
        {
            return Encoding.UTF8.GetBytes(text);
        }

        string key = keys[random.Next(keys.Length)];
        if (random.Next(5) == 0)
        {
            target.Remove(key);
        }
        else
        {
            target[key] = random.Next(30) switch
            {
                0 => null,
                1 => double.MaxValue,
                2 => -1,
                3 => "texto",
                4 => new JsonArray(),
                5 => new JsonObject(),
                6 => new string('x', 1 << 20),
                7 => 1e-320,
                8 => long.MinValue,
                9 => "a" + EscapeMarker + "b",
                < 20 => true,
                _ => "ok",
            };
        }

        return Encoding.UTF8.GetBytes(Escapes(random, root.ToJsonString()));
    }
}

/// <summary>
/// Colección compartida con <c>CubeReaderTests</c>, que mide 65³ contra 150 ms: dentro de una colección las clases no
/// corren a la vez, y el fuzz (que usa todos los núcleos) no le sube el tiempo.
/// </summary>
public static class TimingGroup
{
    public const string Name = "Pruebas de tiempo y fuzz";
}
