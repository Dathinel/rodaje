using Rodaje.Packages.Content;

namespace Rodaje.Packages.Tests;

/// <summary>
/// Contrato de la sección «Referencias estables» de <c>docs/formato-paquete.md</c>: una prueba por promesa, con la frase del
/// documento encima. Lo de arrastrar desde la biblioteca y cómo se muestra el efecto viven en la app, no aquí.
/// </summary>
public sealed class ReferenceContractTests : IDisposable
{
    private const string Example = "rodaje-transiciones@1.2.0/deslizar";
    private readonly TempFolder _folder = new();
    private readonly PackageLocations _locations;

    public ReferenceContractTests() => _locations = PackageLocations.Under(_folder.Path);

    public void Dispose() => _folder.Dispose();

    private void Install(string version)
    {
        string root = Path.Combine(_locations.Packages, "rodaje-transiciones");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "paquete.json"), Samples.Manifest(version: version, items: """
            [ { "id": "deslizar", "kind": "transition", "name": "Deslizar", "file": "deslizar.json" },
              { "id": "cine", "kind": "filter", "name": "Cine", "file": "cine.json" },
              { "id": "entrada", "kind": "effect", "name": "Entrada", "file": "entrada.json" },
              { "id": "lut-cine", "kind": "lut", "name": "Cine", "file": "cine.cube" } ]
            """));
        File.WriteAllText(Path.Combine(root, "deslizar.json"), """
            { "format": "rodaje.preset", "version": 2, "name": "Deslizar", "durationSeconds": 0.5,
              "incoming": { "x": [ { "t": 0, "value": 1920 }, { "t": 0.5, "value": 0 } ] } }
            """);
        File.WriteAllText(Path.Combine(root, "cine.json"), """
            { "format": "rodaje.filter", "version": 1, "name": "Cine", "effects": [ { "type": "rodaje.color", "options": { "lut": "./lut-cine" } } ] }
            """);
        File.WriteAllText(Path.Combine(root, "entrada.json"), """
            { "format": "rodaje.effect", "version": 1, "name": "Entrada", "anchor": "start", "durationSeconds": 0.5,
              "curves": [ { "effect": "rodaje.transform", "parameter": "opacity", "keys": [ { "t": 0, "value": 0 }, { "t": 0.5, "value": 1 } ] } ] }
            """);
        File.WriteAllText(Path.Combine(root, "cine.cube"), CubeReaderTests.Identity(2));
    }

    private PackageScan Scan() => PackageLibrary.Scan(_locations, "rodaje");

    // «Un proyecto que usa un elemento de un paquete guarda `paquete@versión/elemento`, por ejemplo
    // `rodaje-transiciones@1.2.0/deslizar` (`PackageReference`).»
    [Fact]
    public void El_ejemplo_del_documento_es_una_referencia_y_vuelve_tal_cual()
    {
        Assert.True(PackageReference.TryParse(Example, out PackageReference? reference));
        Assert.Equal("rodaje-transiciones", reference.PackageId);
        Assert.Equal(PackageVersion.Parse("1.2.0"), reference.Version);
        Assert.Equal("deslizar", reference.ItemId);
        Assert.Equal(Example, reference.ToString());
    }

    // «Un proyecto que usa un elemento de un paquete guarda `paquete@versión/elemento`»
    [Fact]
    public void La_referencia_de_un_elemento_instalado_es_paquete_version_elemento()
    {
        Install("1.2.0");
        PackageItemEntry entry = Assert.Single(Scan().Items(ItemKind.Transition));
        Assert.Equal(Example, entry.Reference.ToString());
        Assert.True(PackageReference.TryParse(entry.Reference.ToString(), out PackageReference? again));
        Assert.Equal(entry.Reference, again);
    }

    // «Un proyecto que usa un elemento de un paquete guarda `paquete@versión/elemento`»: la versión se guarda como se
    // escribió en el manifiesto, también con prelanzamiento y compilación.
    [Theory]
    [InlineData("1.2.0-beta.1")]
    [InlineData("1.2.0+20261015")]
    [InlineData("1.2.0-rc.2+abc")]
    public void La_version_de_la_referencia_vuelve_como_se_escribio(string version)
    {
        Install(version);
        string text = Assert.Single(Scan().Items(ItemKind.Transition)).Reference.ToString();
        Assert.Equal($"rodaje-transiciones@{version}/deslizar", text);
        Assert.True(PackageReference.TryParse(text, out PackageReference? reference));
        Assert.Equal(text, reference.ToString());
    }

    // «Si al abrirlo el paquete no está instalado, el proyecto abre igual y el efecto se muestra como «falta el paquete»,
    // con el id para instalarlo.»
    [Fact]
    public async Task Sin_el_paquete_ningun_elemento_falla_y_el_motivo_nombra_el_id()
    {
        using var store = new PackageContentStore(Scan());
        Assert.Null(store.FindTransition(Example));
        Assert.Null(store.FindFilter("rodaje-transiciones@1.2.0/cine"));
        Assert.Null(store.FindEffect("rodaje-transiciones@1.2.0/entrada"));
        Assert.False(store.TryGetLut("rodaje-transiciones@1.2.0/lut-cine", out _));
        await store.WhenLoaded();
        Assert.False(store.TryGetLut("rodaje-transiciones@1.2.0/lut-cine", out _));

        const string Expected = "Falta el paquete «rodaje-transiciones» (1.2.0).";
        Assert.Equal(Expected, store.PresetProblem(Example));
        Assert.Equal(Expected, store.PresetProblem("rodaje-transiciones@1.2.0/cine"));
        Assert.Equal(Expected, store.PresetProblem("rodaje-transiciones@1.2.0/entrada"));
        Assert.Equal(Expected, store.LutProblem("rodaje-transiciones@1.2.0/lut-cine"));
    }

    // «con el id para instalarlo»: el id del motivo es el de la referencia, que es el que busca el catálogo.
    [Fact]
    public void El_id_del_motivo_es_un_id_de_paquete_valido()
    {
        using var store = new PackageContentStore(Scan());
        Assert.Null(store.FindTransition(Example));
        string problem = store.PresetProblem(Example)!;
        int open = problem.IndexOf('«', StringComparison.Ordinal);
        int close = problem.IndexOf('»', StringComparison.Ordinal);
        string id = problem[(open + 1)..close];
        Assert.True(Identifiers.IsValid(id));
        Assert.True(PackageReference.TryParse(Example, out PackageReference? reference));
        Assert.Equal(reference.PackageId, id);
    }

    // «Si al abrirlo el paquete no está instalado […]»: al instalarlo, la misma referencia guardada vuelve a encontrar el
    // elemento sin tocar el proyecto.
    [Fact]
    public void Al_instalar_el_paquete_la_misma_referencia_se_resuelve()
    {
        using var store = new PackageContentStore(Scan());
        Assert.Null(store.FindTransition(Example));
        Install("1.2.0");
        store.Reload(Scan());
        Assert.NotNull(store.FindTransition(Example));
        Assert.Null(store.PresetProblem(Example));
        FilterPreset filter = store.FindFilter("rodaje-transiciones@1.2.0/cine")!;
        Assert.Equal("rodaje-transiciones@1.2.0/lut-cine", Assert.Single(filter.Effects).Options["lut"]);
    }
}
