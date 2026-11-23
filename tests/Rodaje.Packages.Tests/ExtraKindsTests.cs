namespace Rodaje.Packages.Tests;

public sealed class ExtraKindsTests
{
    [Fact]
    public void Recuperacion_tras_apagon_conserva_opciones_de_la_aplicacion()
    {
        string root = Path.Combine(Path.GetTempPath(), "rodaje-extra-" + Guid.NewGuid().ToString("N"));
        var locations = PackageLocations.Under(root);
        string aside = Path.Combine(locations.Packages, ".anterior-0123456789ab-mi-pieza");
        Directory.CreateDirectory(aside);
        try
        {
            File.WriteAllText(Path.Combine(aside, "paquete.json"), """
                {"formatVersion":1,"id":"mi-pieza","version":"1.0.0","kind":"content",
                 "name":"Pieza","author":"","license":"CC-BY-4.0","apps":["unicomp"],
                 "items":[{"id":"catalogo","kind":"component","name":"Pieza","file":"catalogo.json"}]}
                """);
            File.WriteAllText(Path.Combine(aside, "catalogo.json"), "{}");
            var options = new ReadOptions { AllowEmptyAuthor = true, ExtraKinds = new HashSet<string>(StringComparer.Ordinal) { "component" } };
            var installer = new PackageInstaller(locations, "unicomp", readOptions: options);
            Assert.Equal(1, installer.CleanUpLeftovers());
            Assert.True(File.Exists(Path.Combine(locations.Packages, "mi-pieza", "catalogo.json")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Sin_declaracion_se_omite_y_el_tipo_no_elude_rutas_seguras()
    {
        const string json = """
            {"formatVersion":1,"id":"unicomp-sensores","version":"1.0.0",
             "name":"Sensores","author":"Prueba","license":"CC-BY-4.0","apps":["unicomp"],
             "items":[{"id":"catalogo","kind":"component","name":"Sensores","file":"../catalogo.json"}]}
            """;
        Assert.Empty(ManifestReader.Read(json).Manifest!.Items);
        var accepted = ManifestReader.Read(json, options: new ReadOptions { ExtraKinds = new HashSet<string>(StringComparer.Ordinal) { "component" } });
        Assert.False(accepted.IsValid);
        Assert.NotEmpty(accepted.Errors);
    }
    [Fact]
    public void Unicomp_conserva_el_tipo_component_declarado()
    {
        var result = ManifestReader.Read("""
            {"formatVersion":1,"id":"unicomp-sensores","version":"1.0.0",
             "name":"Sensores","author":"Prueba","license":"CC-BY-4.0","apps":["unicomp"],
             "items":[{"id":"catalogo","kind":"component","name":"Sensores","file":"catalogo.json"}]}
            """, options: new ReadOptions { ExtraKinds = new HashSet<string>(StringComparer.Ordinal) { "component" } });
        Assert.True(result.IsValid, string.Join(" ", result.Errors));
        var item = Assert.Single(result.Manifest!.Items);
        Assert.Equal(ItemKind.Other, item.Kind);
        Assert.Equal("component", item.OtherKind);
    }
}
