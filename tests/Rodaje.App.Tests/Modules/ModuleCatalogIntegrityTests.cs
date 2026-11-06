using Rodaje.App.Services.Modules;
using Rodaje.App.Tests.Support;
using Rodaje.Packages;

namespace Rodaje.App.Tests.Modules;

/// <summary>
/// Integridad de módulos instalados: la primera exploración escribe <c>integridad.json</c> (base para los
/// módulos instalados por versiones anteriores) y desde entonces cualquier cambio en los archivos deja el
/// módulo fuera con un problema, sin tumbar el listado del resto.
/// </summary>
public sealed class ModuleCatalogIntegrityTests : IDisposable
{
    private readonly TempDirectory _root = new();

    public void Dispose() => _root.Dispose();

    [Fact]
    public void LaPrimeraExploracion_EscribeElManifiesto_YLaSiguienteDetectaLaManipulacion()
    {
        Write("modulo-bueno");

        var problems = new List<string>();
        IReadOnlyList<ModuleDescriptor> modules = Scan(problems);
        Assert.Empty(problems);
        Assert.Single(modules);
        Assert.True(File.Exists(Path.Combine(_root.Path, "modulo-bueno", PackageIntegrity.FileName)));

        File.WriteAllText(Path.Combine(_root.Path, "modulo-bueno", "modulo.exe"), "troyanizado");

        problems = [];
        modules = Scan(problems);
        Assert.Empty(modules);
        Assert.Contains(problems, p => p.Contains("modulo-bueno") && p.Contains("integridad", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void UnManifiestoEnorme_NoSeLista_NiTumbaLosDemas()
    {
        Write("modulo-enorme", manifestBytes: ModuleCatalog.MaxManifestBytes + 1);
        Write("modulo-bueno");

        var problems = new List<string>();
        IReadOnlyList<ModuleDescriptor> modules = Scan(problems);

        Assert.Single(modules);
        Assert.Equal("modulo-bueno", modules[0].Id);
        Assert.Contains(problems, p => p.Contains("modulo-enorme"));
    }

    private IReadOnlyList<ModuleDescriptor> Scan(List<string> problems) =>
        ModuleCatalog.Scan([new ModuleRoot(_root.Path, ModuleOrigin.Installed)], problems);

    private void Write(string id, int? manifestBytes = null)
    {
        string dir = Path.Combine(_root.Path, id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "modulo.exe"), "exe");
        string manifest = $$"""
            { "formatVersion": 1, "id": "{{id}}", "version": "1.0.0", "kind": "module", "name": "Módulo {{id}}", "author": "Pruebas",
              "license": "GPL-3.0-only", "apps": ["rodaje"],
              "module": { "entry": "modulo.exe", "protocol": 1, "functions": ["eco"] } }
            """;
        if (manifestBytes is { } bytes)
        {
            // Se rellena con espacio en blanco JSON válido: importa el tamaño, no el contenido.
            manifest = manifest.Insert(manifest.LastIndexOf('}'), new string(' ', bytes - manifest.Length));
        }
        File.WriteAllText(Path.Combine(dir, "paquete.json"), manifest);
    }
}
