using Rodaje.App.Services.Inspector;
using Rodaje.App.Services.Packages;
using Rodaje.App.Services.SafeZones;
using Rodaje.App.Services.Titles;
using Rodaje.App.Tests.Support;
using Rodaje.Core.Model;
using Rodaje.Packages;
using Rodaje.Packages.Content;

namespace Rodaje.App.Tests.Packages;

/// <summary>
/// Un elemento propio sin <c>id</c> explícito que la v1.0.0 conocía por su id antiguo (<c>m-sica-pica-and</c> para «Música
/// épica Ñandú») se sigue eligiendo por él en las listas que comparan la referencia (tarea 264, sigue a #228).
/// </summary>
public sealed class LegacyIdAliasTests : IDisposable
{
    private const string Legacy = "propios@0.0.0/m-sica-pica-and";
    private const string Current = "propios@0.0.0/musica-epica-nandu";
    private const string Stem = "Música épica Ñandú";

    private readonly TempDirectory _folder = new();

    public void Dispose() => _folder.Dispose();

    [Fact]
    public void Una_zona_segura_propia_se_elige_por_su_id_antiguo()
    {
        var preset = new SafeZonePreset("", Stem, 9, 16, [new SafeZone("Logo", SafeZoneKind.Interface, 0.8, 0.02, 0.18, 0.08)], false);
        PackageScan scan = Own("zonas", ContentReaders.WriteSafeZones(preset, "safe-zones"));

        IReadOnlyList<SafeZoneChoice> all = SafeZoneCatalog.All(scan);

        Assert.Equal(Current, SafeZoneCatalog.Find(all, Current).Reference);
        Assert.Equal(Current, SafeZoneCatalog.Find(all, Legacy).Reference);
        Assert.Equal(SafeZoneCatalog.Classic, SafeZoneCatalog.Find(all, null).Reference);
    }

    [Fact]
    public void Un_estilo_de_texto_propio_se_elige_por_su_id_antiguo()
    {
        PackageScan scan = Own("estilos", """{ "kind": "text-style" }"""u8.ToArray());
        ChoiceList<LibraryElement> styles = TextStyleChoices.Build(scan);
        TextStyle style = TitleStyles.Default();
        style.Source = Legacy;

        int index = TextStyleChoices.IndexOf(styles, style);

        Assert.True(index >= 0);
        Assert.Equal(Current, styles.Values[index].Reference);
    }

    [Fact]
    public void Una_LUT_propia_que_usa_un_filtro_por_su_id_antiguo_no_sale_repetida()
    {
        Own("luts", """{ "kind": "lut" }"""u8.ToArray());
        PackageScan scan = Own("filtros", """
            {
              "kind": "filter", "format": "rodaje.filter", "version": 1, "name": "Cálido mío",
              "effects": [{ "type": "rodaje.color", "parameters": { "lutIntensity": 1 }, "options": { "lut": "propios@0.0.0/m-sica-pica-and" } }]
            }
            """u8.ToArray(), "Cálido mío.json");

        IReadOnlyList<LibraryElement> filters = LibraryElements.For(LibraryCategory.Filters, scan);

        Assert.Contains(filters, f => f.Name == "Cálido mío");
        Assert.DoesNotContain(filters, f => f.Reference == Current);
    }

    [Fact]
    public void Una_transicion_propia_se_elige_por_su_id_antiguo_sin_falta_el_paquete()
    {
        PackageScan scan = Own("transiciones", """{ "kind": "transition" }"""u8.ToArray());

        (ChoiceList<(string Type, string Preset)> types, int index) = TransitionChoices.Types(TrackKind.Video, scan, BuiltInTransitions.Preset, Legacy);

        Assert.Equal(Current, types.Values[index].Preset);
        Assert.DoesNotContain(types.Labels, l => l.Contains("falta", StringComparison.Ordinal));
    }

    [Fact]
    public void Una_LUT_propia_se_elige_por_su_id_antiguo_sin_falta_el_paquete()
    {
        PackageScan scan = Own("luts", """{ "kind": "lut" }"""u8.ToArray());

        (ChoiceList<string?> luts, int index) = LutChoices.Build(scan, Legacy);

        Assert.Equal(Current, luts.Values[index]);
        Assert.DoesNotContain(luts.Labels, l => l.Contains("falta", StringComparison.Ordinal));
    }

    private PackageScan Own(string folder, byte[] content, string file = Stem + ".json")
    {
        var locations = PackageLocations.Under(_folder.Path);
        Directory.CreateDirectory(Path.Combine(locations.Own, folder));
        File.WriteAllBytes(Path.Combine(locations.Own, folder, file), content);
        PackageScan scan = PackageLibrary.Scan(locations, "rodaje");
        Assert.Equal("m-sica-pica-and", scan.Find(PackageLibrary.OwnPackageId)!.Manifest.Items.First(i => i.Name == Stem).LegacyId);
        return scan;
    }
}
