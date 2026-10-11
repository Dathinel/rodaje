using Rodaje.Paquetes.Content;

namespace Rodaje.Paquetes.Tests;

public sealed class PackageContentStoreTests : IDisposable
{
    private const string Lut = "looks@1.0.0/calido-lut";
    private readonly TempFolder _folder = new();
    private readonly PackageLocations _locations;

    public PackageContentStoreTests()
    {
        _locations = PackageLocations.Under(_folder.Path);
        Install("looks", "1.0.0");
    }

    public void Dispose() => _folder.Dispose();

    private void Install(string id, string version, string cube = "")
    {
        string root = Path.Combine(_locations.Packages, id);
        Directory.CreateDirectory(Path.Combine(root, "luts"));
        File.WriteAllText(Path.Combine(root, "paquete.json"), Samples.Manifest(id, version, items: """
            [ { "id": "calido-lut", "kind": "lut", "name": "Cálido", "file": "luts/calido.cube" },
              { "id": "danada", "kind": "lut", "name": "Dañada", "file": "luts/danada.cube" },
              { "id": "calido", "kind": "filter", "name": "Cálido", "file": "filtro.json" },
              { "id": "zoom", "kind": "transition", "name": "Zoom", "file": "zoom.json" },
              { "id": "rota", "kind": "transition", "name": "Rota", "file": "rota.json" },
              { "id": "entrada", "kind": "effect", "name": "Entrada", "file": "entrada.json" } ]
            """));
        File.WriteAllText(Path.Combine(root, "luts", "calido.cube"), cube.Length > 0 ? cube : CubeReaderTests.Identity(5));
        File.WriteAllText(Path.Combine(root, "luts", "danada.cube"), "LUT_1D_SIZE 4\n");
        File.WriteAllText(Path.Combine(root, "filtro.json"), """
            { "format": "rodaje.filter", "version": 1, "name": "Cálido",
              "effects": [ { "type": "rodaje.color", "parameters": { "temperature": 0.3 }, "options": { "lut": "./calido-lut", "otra": "x@1.0.0/y", "mala": "./No Vale" } } ] }
            """);
        File.WriteAllText(Path.Combine(root, "entrada.json"), """
            { "format": "rodaje.effect", "version": 1, "name": "Entrada", "anchor": "start", "durationSeconds": 0.5,
              "effects": [ { "type": "rodaje.color", "options": { "lut": "./calido-lut" } } ],
              "curves": [ { "effect": "rodaje.transform", "parameter": "opacity", "keys": [ { "t": 0, "value": 0 }, { "t": 0.5, "value": 1 } ] } ] }
            """);
        File.WriteAllText(Path.Combine(root, "zoom.json"), """
            { "format": "rodaje.preset", "version": 2, "name": "Zoom", "durationSeconds": 0.5,
              "incoming": { "scale": [ { "t": 0, "value": 2 }, { "t": 0.5, "value": 1 } ] } }
            """);
        File.WriteAllText(Path.Combine(root, "rota.json"), "{ no es json");
    }

    private PackageScan Scan() => PackageLibrary.Scan(_locations, "rodaje");

    private static async Task<LutTable> LoadAsync(PackageContentStore store, string reference)
    {
        Assert.False(store.TryGetLut(reference, out _));
        await store.WhenLoaded();
        Assert.True(store.TryGetLut(reference, out LutTable? lut));
        return lut;
    }

    [Fact]
    public async Task Carga_la_LUT_fuera_del_hilo_y_avisa_al_terminar()
    {
        using var store = new PackageContentStore(Scan());
        int changes = 0;
        store.Changed += (_, _) => Interlocked.Increment(ref changes);
        LutTable lut = await LoadAsync(store, Lut);
        Assert.Equal(5, lut.Size);
        Assert.Equal(1, changes);
        Assert.Equal(5 * 5 * 5 * 3 * sizeof(float), store.LutBytes);
        Assert.True(store.TryGetLut(Lut, out LutTable? again));
        Assert.Same(lut, again);
        Assert.Null(store.LutProblem(Lut));
    }

    [Fact]
    public async Task Usa_la_version_instalada_aunque_la_referencia_sea_otra()
    {
        using var store = new PackageContentStore(Scan());
        Assert.Equal(5, (await LoadAsync(store, "looks@0.9.0/calido-lut")).Size);
    }

    [Theory]
    [InlineData("otro@1.0.0/calido-lut", "Falta el paquete «otro» (1.0.0).")]
    [InlineData("looks@1.0.0/frio", "El paquete «looks» no trae «frio».")]
    [InlineData("looks@1.0.0/zoom", "«zoom» de «looks» no es una LUT.")]
    [InlineData("no es referencia", "no es una referencia paquete@versión/elemento")]
    public async Task Sin_paquete_o_elemento_explica_por_que(string reference, string problem)
    {
        using var store = new PackageContentStore(Scan());
        Assert.False(store.TryGetLut(reference, out _));
        await store.WhenLoaded();
        Assert.False(store.TryGetLut(reference, out _));
        Assert.Contains(problem, store.LutProblem(reference), StringComparison.Ordinal);
        Assert.False(store.TryGetLut("", out _));
    }

    [Fact]
    public async Task Una_LUT_dañada_no_se_reintenta_y_dice_el_motivo()
    {
        using var store = new PackageContentStore(Scan());
        Assert.False(store.TryGetLut("looks@1.0.0/danada", out _));
        await store.WhenLoaded();
        Assert.False(store.TryGetLut("looks@1.0.0/danada", out _));
        Assert.Contains("LUT 1D", store.LutProblem("looks@1.0.0/danada"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Respeta_el_tope_de_memoria_soltando_la_menos_usada()
    {
        Install("otros", "1.0.0");
        long one = 5 * 5 * 5 * 3 * sizeof(float);
        using var store = new PackageContentStore(Scan(), lutBudgetBytes: one);
        await LoadAsync(store, Lut);
        await LoadAsync(store, "otros@1.0.0/calido-lut");
        Assert.Equal(one, store.LutBytes);
        Assert.False(store.TryGetLut(Lut, out _));
        await store.WhenLoaded();
        Assert.True(store.TryGetLut(Lut, out _));
    }

    [Fact]
    public async Task Al_desinstalar_y_volver_a_instalar_recarga()
    {
        using var store = new PackageContentStore(Scan());
        await LoadAsync(store, Lut);
        Assert.NotNull(store.FindTransition("looks@1.0.0/zoom"));
        int changes = 0;
        store.Changed += (_, _) => changes++;

        Directory.Delete(Path.Combine(_locations.Packages, "looks"), recursive: true);
        store.Reload(Scan());
        Assert.Equal(1, changes);
        Assert.Equal(0, store.LutBytes);
        Assert.False(store.TryGetLut(Lut, out _));
        Assert.Contains("Falta el paquete", store.LutProblem(Lut), StringComparison.Ordinal);
        Assert.Null(store.FindTransition("looks@1.0.0/zoom"));

        Install("looks", "1.1.0", CubeReaderTests.Identity(3));
        store.Reload(Scan());
        Assert.Equal(3, (await LoadAsync(store, Lut)).Size);
        Assert.NotNull(store.FindTransition("looks@1.0.0/zoom"));
    }

    [Fact]
    public async Task Una_carga_de_antes_de_recargar_no_se_guarda()
    {
        using var store = new PackageContentStore(Scan());
        Assert.False(store.TryGetLut(Lut, out _));
        store.Reload(PackageScan.Empty);
        await store.WhenLoaded();
        Assert.False(store.TryGetLut(Lut, out _));
        Assert.Equal(0, store.LutBytes);
    }

    [Fact]
    public void Lee_y_guarda_transiciones()
    {
        using var store = new PackageContentStore(Scan());
        TransitionPreset? zoom = store.FindTransition("looks@1.0.0/zoom");
        Assert.NotNull(zoom);
        Assert.Same(zoom, store.FindTransition("looks@1.0.0/zoom"));
        Assert.Equal(2, zoom.Incoming[0].Keys[0].Value);
        Assert.Null(store.PresetProblem("looks@1.0.0/zoom"));

        Assert.Null(store.FindTransition("looks@1.0.0/rota"));
        Assert.Contains("«Rota»: No es JSON válido", store.PresetProblem("looks@1.0.0/rota"), StringComparison.Ordinal);
        Assert.Null(store.FindTransition("looks@1.0.0/calido"));
        Assert.Contains("no es una transición", store.PresetProblem("looks@1.0.0/calido"), StringComparison.Ordinal);
        Assert.Null(store.FindTransition(""));
    }

    [Fact]
    public void Los_filtros_y_efectos_nombran_sus_LUT_con_la_referencia_completa()
    {
        using var store = new PackageContentStore(Scan());
        FilterPreset? filter = store.FindFilter("looks@1.0.0/calido");
        Assert.NotNull(filter);
        IReadOnlyDictionary<string, string> options = filter.Effects[0].Options;
        Assert.Equal("looks@1.0.0/calido-lut", options["lut"]);
        Assert.Equal("x@1.0.0/y", options["otra"]);
        Assert.Equal("./No Vale", options["mala"]);
        Assert.Equal(0.3, filter.Effects[0].Parameters["temperature"]);

        EffectPreset? effect = store.FindEffect("looks@1.0.0/entrada");
        Assert.NotNull(effect);
        Assert.Equal("looks@1.0.0/calido-lut", effect.Effects[0].Options["lut"]);
        Assert.Null(store.FindEffect("looks@1.0.0/calido"));
    }

    [Fact]
    public async Task Tras_desecharse_no_carga_nada()
    {
        var store = new PackageContentStore(Scan());
        store.Dispose();
        Assert.False(store.TryGetLut(Lut, out _));
        await store.WhenLoaded();
        Assert.Null(store.FindTransition("looks@1.0.0/zoom"));
    }
}
