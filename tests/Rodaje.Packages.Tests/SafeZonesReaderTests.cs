using System.Text;
using Rodaje.Packages.Content;

namespace Rodaje.Packages.Tests;

/// <summary><c>rodaje.safezones</c> v1: lectura, valores por defecto, rechazos, ida y vuelta con el escritor y como elemento propio.</summary>
public sealed class SafeZonesReaderTests
{
    private const string Reels = """
        { "format": "rodaje.safezones", "version": 1, "id": "reels", "name": "Reels",
          "aspect": { "width": 9, "height": 16 },
          "zones": [
            { "label": "Botones", "rect": [0.86, 0.5, 0.14, 0.38] },
            { "label": "Nombre y pie", "kind": "interface", "rect": [0, 0.78, 0.84, 0.17] },
            { "label": "Títulos", "kind": "safe", "rect": [0.05, 0.05, 0.9, 0.9] } ],
          "otro": "se ignora" }
        """;

    private static MemoryStream Utf8(string json) => new(Encoding.UTF8.GetBytes(json));

    private static string Error(string json) =>
        Assert.Throws<PackageContentException>(() => ContentReaders.ReadSafeZones(Utf8(json))).Message;

    private static string With(string zones, string extra = "") =>
        $$"""{ "format": "rodaje.safezones", "version": 1, "name": "Z"{{extra}}, "zones": [ {{zones}} ] }""";

    [Fact]
    public void Lee_las_zonas_con_su_forma()
    {
        SafeZonePreset preset = ContentReaders.ReadSafeZones(Utf8(Reels));
        Assert.Equal(("reels", "Reels", 9, 16, false), (preset.Id, preset.Name, preset.AspectWidth, preset.AspectHeight, preset.Center));
        Assert.Equal(3, preset.Zones.Count);
        Assert.Equal(new SafeZone("Botones", SafeZoneKind.Interface, 0.86, 0.5, 0.14, 0.38), preset.Zones[0]);
        Assert.Equal(SafeZoneKind.Safe, preset.Zones[2].Kind);
        Assert.Equal("9:16", preset.AspectText);
        Assert.True(preset.Fits(1080, 1920));
        Assert.True(preset.Fits(720, 1280));
        Assert.False(preset.Fits(1920, 1080));
        Assert.False(preset.Fits(1080, 1350));
    }

    [Fact]
    public void Sin_forma_sirve_para_cualquiera()
    {
        SafeZonePreset preset = ContentReaders.ReadSafeZones(Utf8(With("""{ "label": "Acción", "kind": "safe", "rect": [0.035, 0.035, 0.93, 0.93] }""", """, "center": true""")));
        Assert.Equal((0, 0, true, ""), (preset.AspectWidth, preset.AspectHeight, preset.Center, preset.AspectText));
        Assert.True(preset.Fits(1920, 1080) && preset.Fits(1080, 1920));
    }

    [Theory]
    [InlineData("""{ "label": "x", "rect": [0.5, 0, 0.6, 0.1] }""", "se sale de la imagen")]
    [InlineData("""{ "label": "x", "rect": [-0.1, 0, 0.5, 0.1] }""", "se sale de la imagen")]
    [InlineData("""{ "label": "x", "rect": [0, 0, 0, 0.1] }""", "se sale de la imagen")]
    [InlineData("""{ "label": "x", "rect": [0, 0, 0.5] }""", "[x, y, ancho, alto]")]
    [InlineData("""{ "label": "x", "rect": [0, 0, "a", 0.1] }""", "rect[2] tiene que ser un número")]
    [InlineData("""{ "rect": [0, 0, 0.5, 0.1] }""", "label tiene que tener")]
    [InlineData("""{ "label": "x", "kind": "boton", "rect": [0, 0, 0.5, 0.1] }""", "kind «boton» no existe")]
    [InlineData("3", "zones[0] tiene que ser un objeto")]
    public void Rechaza_zonas_mal_hechas(string zone, string expected) =>
        Assert.Contains(expected, Error(With(zone)), StringComparison.Ordinal);

    [Fact]
    public void Rechaza_lo_demas_con_su_motivo()
    {
        const string one = """{ "label": "x", "rect": [0, 0, 0.1, 0.1] }""";
        Assert.Contains("de 1 a 32 zonas", Error(With("")), StringComparison.Ordinal);
        Assert.Contains("de 1 a 32 zonas", Error(With(string.Join(", ", Enumerable.Repeat(one, 33)))), StringComparison.Ordinal);
        Assert.Contains("aspect.width queda fuera de rango", Error(With(one, """, "aspect": { "width": 0, "height": 16 }""")), StringComparison.Ordinal);
        Assert.Contains("center tiene que ser", Error(With(one, """, "center": 1""")), StringComparison.Ordinal);
        Assert.Contains("actualiza el programa", Error(Reels.Replace("\"version\": 1", "\"version\": 2", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.Contains("Falta zones", Error("""{ "format": "rodaje.safezones", "version": 1, "name": "Z" }"""), StringComparison.Ordinal);
    }

    [Fact]
    public void El_escritor_da_un_archivo_que_se_vuelve_a_leer_igual()
    {
        SafeZonePreset original = ContentReaders.ReadSafeZones(Utf8(Reels)) with { Center = true };
        byte[] json = ContentReaders.WriteSafeZones(original, "safe-zones");
        SafeZonePreset back = ContentReaders.ReadSafeZones(new MemoryStream(json));
        Assert.Equal(original with { Zones = [] }, back with { Zones = [] });
        Assert.Equal(original.Zones, back.Zones);
        Assert.Contains("\"kind\": \"safe-zones\"", Encoding.UTF8.GetString(json), StringComparison.Ordinal);
    }

    [Fact]
    public void Un_archivo_suelto_en_propios_es_un_elemento_de_zonas()
    {
        using var temp = new TempFolder();
        PackageLocations locations = PackageLocations.Under(temp.Path);
        Directory.CreateDirectory(locations.Own);
        SafeZonePreset mine = new("propios.mi-canal", "Mi canal", 9, 16, [new SafeZone("Logo", SafeZoneKind.Interface, 0.8, 0.02, 0.18, 0.08)], false);
        File.WriteAllBytes(Path.Combine(locations.Own, "Mi canal.json"), ContentReaders.WriteSafeZones(mine, "safe-zones"));

        PackageItemEntry entry = PackageLibrary.Scan(locations, "rodaje").Items(ItemKind.SafeZones).Single();

        Assert.Equal("propios@0.0.0/mi-canal", entry.Reference.ToString());
        Assert.Equal("Mi canal", entry.Item.Name);
        using FileStream stream = File.OpenRead(entry.FilePath!);
        Assert.Equal("Logo", ContentReaders.ReadSafeZones(stream).Zones[0].Label);
    }
}
