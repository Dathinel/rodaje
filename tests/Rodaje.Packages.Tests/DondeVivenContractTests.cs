using Rodaje.Packages.Content;
using Rodaje.Packages.Download;

namespace Rodaje.Packages.Tests;

/// <summary>
/// Contrato de la sección «Dónde viven» de <c>docs/formato-paquete.md</c>: una prueba por promesa, con la frase del
/// documento encima.
/// </summary>
public sealed class DondeVivenContractTests : IDisposable
{
    private const string ModuleExtra = ",\n\"module\": { \"entry\": \"bin/modulo.exe\", \"protocol\": 1, \"functions\": [\"voz\"] }";

    // El ejemplo del documento, tal cual.
    private const string InspectorTransition = """
        { "format": "rodaje.preset", "version": 1, "id": "propios.zoom-de-entrada", "name": "Zoom de entrada", "kind": "transition",
          "effect": "rodaje.transform", "durationSeconds": 0.5, "reference": { "width": 1920, "height": 1080 },
          "tracks": { "scale": [ { "t": 0, "value": 1.2, "interp": "easeOut" }, { "t": 0.5, "value": 1, "interp": "linear" } ] } }
        """;

    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private string Data => _folder.Combine("complementos");

    private string Bundled => _folder.Combine("app", "paquetes");

    private PackageLocations Locations => PackageLocations.Under(Data, [Bundled]);

    private static void WritePackage(string directory, string id, string version, string kind = "content")
    {
        Directory.CreateDirectory(Path.Combine(directory, "transiciones"));
        File.WriteAllText(Path.Combine(directory, "transiciones", "deslizar.json"), "{}");
        if (kind == "module")
        {
            Directory.CreateDirectory(Path.Combine(directory, "bin"));
            File.WriteAllText(Path.Combine(directory, "bin", "modulo.exe"), "x");
        }

        File.WriteAllText(Path.Combine(directory, PackageManifest.FileName), kind == "module"
            ? Samples.Manifest(id, version, kind: kind, items: "[]", extra: ModuleExtra)
            : Samples.Manifest(id, version));
    }

    private void WriteOwn(string relative, string json)
    {
        string path = Path.Combine(Locations.Own, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
    }

    private PackageScan Scan() => PackageLibrary.Scan(Locations, "rodaje");

    // ── La tabla ──────────────────────────────────────────────────────────────────────────────────────────────────

    // «`%LOCALAPPDATA%\Rodaje\complementos\paquetes\<id>\` | Paquetes de contenido instalados»
    // «`%LOCALAPPDATA%\Rodaje\complementos\modulos\<id>\` | Módulos de IA instalados»
    // «`%LOCALAPPDATA%\Rodaje\complementos\propios\` | Lo que hace la persona en Rodaje»
    [Fact]
    public void Tabla_las_carpetas_de_datos_bajo_complementos()
    {
        PackageLocations locations = Locations;

        Assert.Equal(Path.Combine(Data, "paquetes"), locations.Packages);
        Assert.Equal(Path.Combine(Data, "modulos"), locations.Modules);
        Assert.Equal(Path.Combine(Data, "propios"), locations.Own);
        Assert.Equal(locations.Packages, locations.InstallRoot(PackageKind.Content));
        Assert.Equal(locations.Modules, locations.InstallRoot(PackageKind.Module));
    }

    // «`paquetes\<id>\` | Paquetes de contenido instalados | El instalador»
    // «`modulos\<id>\` | Módulos de IA instalados […] | El instalador y la descarga de modelos»
    [Fact]
    public void Tabla_el_instalador_deja_cada_paquete_en_su_carpeta_por_id()
    {
        var installer = new PackageInstaller(Locations, "rodaje");
        string content = Samples.ValidPackage(_folder.Combine("c.paquete"), id: "rodaje-transiciones");
        string module = Samples.Zip(_folder.Combine("m.paquete"),
            (PackageManifest.FileName, Samples.Manifest("modulo-voz", kind: "module", items: "[]", extra: ModuleExtra)),
            ("bin/modulo.exe", "x"));

        Assert.Equal(Path.Combine(Locations.Packages, "rodaje-transiciones"), installer.Install(content).Package.Directory);
        Assert.Equal(Path.Combine(Locations.Modules, "modulo-voz"), installer.Install(module).Package.Directory);
        PackageScan scan = Scan();
        Assert.Equal(PackageOrigin.Installed, scan.Find("rodaje-transiciones")!.Origin);
        Assert.Equal(PackageOrigin.Installed, scan.Find("modulo-voz")!.Origin);
        Assert.False(Directory.Exists(Locations.Own));
    }

    // «`modulos\<id>\` | Módulos de IA instalados, con sus modelos en `<file>`»
    // «`modelos\<sha256>` | Almacén de modelos, uno por contenido (enlazados desde cada módulo)»
    [Fact]
    public void Tabla_el_almacen_de_modelos_guarda_cada_uno_por_su_sha256()
    {
        string sha = new('c', 64);
        string folder = Path.Combine(Data, "modelos");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, sha), [1, 2, 3]);
        using var http = new HttpClient();
        var store = new ModelStore(folder, new FileDownloader(http));
        var model = new ModuleModel("voz", "modelos/voz.onnx", 3, sha, new Uri("https://example.com/voz.onnx"), "MIT");
        string moduleDirectory = Path.Combine(Locations.Modules, "modulo-voz");

        Assert.Equal(Path.Combine(store.Folder, sha), store.Find(model.Spec));
        Assert.Equal(Path.Combine(moduleDirectory, "modelos", "voz.onnx"), model.PathIn(moduleDirectory));
    }

    // «`app\paquetes\` y `app\modulos\` (junto a `app\Rodaje.exe`) | Lo que trae la distribución completa | Nadie: solo lectura»
    [Fact]
    public void Tabla_lo_incluido_es_de_solo_lectura()
    {
        string bundledModules = _folder.Combine("app", "modulos");
        WritePackage(Path.Combine(Bundled, "rodaje-titulos"), "rodaje-titulos", "1.0.0");
        WritePackage(Path.Combine(bundledModules, "modulo-voz"), "modulo-voz", "1.0.0", kind: "module");
        var locations = PackageLocations.Under(Data, [Bundled, bundledModules]);

        PackageScan scan = PackageLibrary.Scan(locations, "rodaje");

        foreach (string id in new[] { "rodaje-titulos", "modulo-voz" })
        {
            InstalledPackage package = scan.Find(id)!;
            Assert.Equal(PackageOrigin.Bundled, package.Origin);
            Assert.False(package.CanUninstall);
            Assert.Throws<PackageInstallException>(() => new PackageInstaller(locations, "rodaje").Uninstall(id));
            Assert.True(File.Exists(Path.Combine(package.Directory, PackageManifest.FileName)));
        }
    }

    // ── Prioridad ─────────────────────────────────────────────────────────────────────────────────────────────────

    // «Si un mismo id aparece en varias carpetas, gana la versión mayor»
    [Fact]
    public void Prioridad_gana_la_version_mayor_venga_de_donde_venga()
    {
        WritePackage(Path.Combine(Bundled, "a"), "a", "2.0.0");
        WritePackage(Path.Combine(Locations.Packages, "a"), "a", "1.5.0");
        WritePackage(Path.Combine(Bundled, "b"), "b", "1.0.0");
        WritePackage(Path.Combine(Locations.Packages, "b"), "b", "1.0.1");

        PackageScan scan = Scan();

        Assert.Equal(("2.0.0", PackageOrigin.Bundled), (scan.Find("a")!.Manifest.Version.ToString(), scan.Find("a")!.Origin));
        Assert.Equal(("1.0.1", PackageOrigin.Installed), (scan.Find("b")!.Manifest.Version.ToString(), scan.Find("b")!.Origin));
        Assert.Single(scan.Packages, p => p.Id == "a");
    }

    // «a igual versión, el instalado sobre el incluido»
    [Fact]
    public void Prioridad_a_igual_version_el_instalado()
    {
        string bundledModules = _folder.Combine("app", "modulos");
        WritePackage(Path.Combine(bundledModules, "modulo-voz"), "modulo-voz", "1.0.0", kind: "module");
        var locations = PackageLocations.Under(Data, [Bundled, bundledModules]);
        WritePackage(Path.Combine(locations.Modules, "modulo-voz"), "modulo-voz", "1.0.0", kind: "module");

        InstalledPackage voice = PackageLibrary.Scan(locations, "rodaje").Find("modulo-voz")!;

        Assert.Equal(PackageOrigin.Installed, voice.Origin);
        Assert.Equal(Path.GetFullPath(Path.Combine(locations.Modules, "modulo-voz")), voice.Directory);
    }

    // ── Elementos propios ─────────────────────────────────────────────────────────────────────────────────────────

    // «La carpeta `propios` puede tener su propio `paquete.json` (se lee como un paquete con id `propios`)»
    [Fact]
    public void Propios_un_paquete_json_se_lee_con_id_propios()
    {
        Directory.CreateDirectory(Path.Combine(Locations.Own, "transiciones"));
        File.WriteAllText(Path.Combine(Locations.Own, "transiciones", "deslizar.json"), "{}");
        File.WriteAllText(Path.Combine(Locations.Own, PackageManifest.FileName), Samples.Manifest("otro-id", "1.0.0"));

        InstalledPackage own = Scan().Find(PackageLibrary.OwnPackageId)!;

        Assert.Equal(PackageOrigin.Own, own.Origin);
        Assert.Equal("deslizar", Assert.Single(own.Manifest.Items).Id);
        Assert.Null(Scan().Find("otro-id"));
        Assert.False(own.CanUninstall);
    }

    // «o, lo más simple, archivos `*.json` sueltos en cualquier subcarpeta, uno por elemento, con al menos `kind` y `name`,
    // y opcionalmente `id` y `description`»
    [Fact]
    public void Propios_archivos_sueltos_en_cualquier_subcarpeta_uno_por_elemento()
    {
        WriteOwn("raiz.json", """{ "kind": "transition", "name": "En la raíz" }""");
        WriteOwn(Path.Combine("a", "b", "c", "hondo.json"), """{ "kind": "filter", "name": "Hondo", "id": "muy-hondo", "description": "Uno\nDos" }""");
        WriteOwn("sin-tipo.json", """{ "name": "Sin tipo" }""");
        WriteOwn("tipo-raro.json", """{ "kind": "nada", "name": "Raro" }""");
        WriteOwn("texto.txt", """{ "kind": "transition", "name": "No es json" }""");

        PackageManifest own = Scan().Find(PackageLibrary.OwnPackageId)!.Manifest;

        Assert.Equal(["muy-hondo", "raiz"], own.Items.Select(i => i.Id).Order(StringComparer.Ordinal));
        PackageItem deep = own.FindItem("muy-hondo")!;
        Assert.Equal((ItemKind.Filter, "Hondo", "a/b/c/hondo.json"), (deep.Kind, deep.Name, deep.File));
        Assert.Equal("Uno\nDos", deep.Description);
        Assert.Equal(ItemKind.Transition, own.FindItem("raiz")!.Kind);
        Assert.Null(own.FindItem("raiz")!.Description);
    }

    // «Si no trae `id`, se deriva del nombre del archivo (`Mi Zoom Rápido.json` → `mi-zoom-rapido`)»
    [Fact]
    public void Propios_sin_id_se_deriva_del_nombre_del_archivo()
    {
        WriteOwn("Mi Zoom Rápido.json", """{ "kind": "transition", "name": "Otro nombre" }""");

        PackageItem item = Assert.Single(Scan().Find(PackageLibrary.OwnPackageId)!.Manifest.Items);

        Assert.Equal("mi-zoom-rapido", item.Id);
        Assert.Equal("Otro nombre", item.Name);
    }

    // «un `id` con el prefijo `propios.` lo pierde»
    [Fact]
    public void Propios_el_prefijo_propios_punto_se_pierde()
    {
        WriteOwn("cualquiera.json", """{ "kind": "transition", "name": "Z", "id": "propios.zoom.suave" }""");
        WriteOwn("sin-prefijo.json", """{ "kind": "transition", "name": "Y", "id": "mio.lento" }""");

        PackageManifest own = Scan().Find(PackageLibrary.OwnPackageId)!.Manifest;

        Assert.Equal(["mio.lento", "zoom.suave"], own.Items.Select(i => i.Id).Order(StringComparer.Ordinal));
    }

    // «Su referencia es `propios@0.0.0/<id>`.»
    [Fact]
    public void Propios_la_referencia_es_propios_0_0_0()
    {
        WriteOwn(Path.Combine("efectos", "Destello.json"), """{ "kind": "effect", "name": "Destello" }""");

        PackageItemEntry entry = Assert.Single(Scan().Items(ItemKind.Effect));

        Assert.Equal("propios@0.0.0/destello", entry.Reference.ToString());
        Assert.True(PackageReference.TryParse(entry.Reference.ToString(), out _));
    }

    // «[La biblioteca] los pone aparte»
    [Fact]
    public void Propios_van_aparte_detras_de_los_paquetes()
    {
        WritePackage(Path.Combine(Locations.Packages, "zzz"), "zzz", "1.0.0");
        WritePackage(Path.Combine(Bundled, "aaa"), "aaa", "1.0.0");
        WriteOwn("a.json", """{ "kind": "transition", "name": "A" }""");

        PackageScan scan = Scan();

        Assert.Equal(PackageOrigin.Own, scan.Packages[^1].Origin);
        Assert.Equal(PackageLibrary.OwnPackageId, scan.Packages[^1].Id);
        Assert.All(scan.Packages.SkipLast(1), p => Assert.NotEqual(PackageOrigin.Own, p.Origin));
    }

    // ── Transiciones del inspector ────────────────────────────────────────────────────────────────────────────────

    // «"Guardar como transición" escribe `propios\<nombre>.json` con este formato»
    [Fact]
    public void Inspector_el_ejemplo_guardado_en_propios_se_lista_y_se_lee()
    {
        WriteOwn("Zoom de entrada.json", InspectorTransition);
        PackageScan scan = Scan();

        PackageItemEntry entry = Assert.Single(scan.Items(ItemKind.Transition));
        Assert.Equal("propios@0.0.0/zoom-de-entrada", entry.Reference.ToString());
        Assert.Equal("Zoom de entrada", entry.Item.Name);

        using var store = new PackageContentStore(scan);
        TransitionPreset preset = store.FindTransition(entry.Reference.ToString())!;
        Assert.Null(store.PresetProblem(entry.Reference.ToString()));
        Assert.Equal((0.5, 1920, 1080), (preset.DurationSeconds, preset.ReferenceWidth, preset.ReferenceHeight));
        PresetCurve scale = Assert.Single(preset.Incoming);
        Assert.Equal("scale", scale.Parameter);
        Assert.Equal(2, scale.Keys.Count);
    }

    // «que es también el del archivo de un elemento `transition` dentro de un `.paquete`, así una transición propia se
    // publica tal cual»
    [Fact]
    public void Inspector_la_misma_transicion_se_publica_tal_cual_en_un_paquete()
    {
        var installer = new PackageInstaller(Locations, "rodaje");
        string archive = Samples.Zip(_folder.Combine("t.paquete"),
            (PackageManifest.FileName, Samples.Manifest("mis-transiciones", "1.0.0",
                items: """[{ "id": "zoom-de-entrada", "kind": "transition", "name": "Zoom de entrada", "file": "transiciones/zoom.json" }]""")),
            ("transiciones/zoom.json", InspectorTransition));
        installer.Install(archive);

        using var store = new PackageContentStore(Scan());
        TransitionPreset? preset = store.FindTransition("mis-transiciones@1.0.0/zoom-de-entrada");

        Assert.Null(store.PresetProblem("mis-transiciones@1.0.0/zoom-de-entrada"));
        Assert.NotNull(preset);
        Assert.Single(preset.Incoming);
    }

    // «Es la versión 1 de `rodaje.preset`; los paquetes usan la 2 (abajo), y la 1 se sigue leyendo.»
    [Fact]
    public void Inspector_la_version_1_se_sigue_leyendo_y_la_2_es_la_actual()
    {
        TransitionPreset preset = PresetReader.ReadTransition(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(InspectorTransition)));

        Assert.Equal("propios.zoom-de-entrada", preset.Id);
        Assert.Equal(2, TransitionPreset.Version);
        Assert.Equal("rodaje.preset", TransitionPreset.Format);
    }
}
