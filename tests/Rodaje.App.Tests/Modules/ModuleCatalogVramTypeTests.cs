using Rodaje.App.Services.Modules;
using Rodaje.App.Tests.Support;

namespace Rodaje.App.Tests.Modules;

/// <summary>
/// <c>module.vramMb</c> con otro tipo: <c>TryGetInt32</c> lanzaba <see cref="InvalidOperationException"/>, que el catch de
/// <c>ReadExtras</c> no atrapaba, y listar los módulos fallaba entero por un solo manifiesto.
/// </summary>
public sealed class ModuleCatalogVramTypeTests : IDisposable
{
    private readonly TempDirectory _root = new();

    public void Dispose() => _root.Dispose();

    [Theory]
    [InlineData("\"8\"")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[8]")]
    [InlineData("true")]
    public void UnVramMbQueNoEsNumero_ListaElModuloSinGpu(string vram)
    {
        Write("raro", vram);
        Write("bueno", "1500");

        var problems = new List<string>();
        IReadOnlyList<ModuleDescriptor> modules = ModuleCatalog.Scan([new ModuleRoot(_root.Path, ModuleOrigin.Installed)], problems);

        Assert.Empty(problems);
        Assert.Equal(0, Assert.Single(modules, m => m.Id == "raro").VramMb);
        Assert.Equal(1500, Assert.Single(modules, m => m.Id == "bueno").VramMb);
    }

    private void Write(string id, string vram)
    {
        string dir = Path.Combine(_root.Path, id);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "modulo.exe"), []);
        File.WriteAllText(Path.Combine(dir, "paquete.json"), $$"""
            { "formatVersion": 1, "id": "{{id}}", "version": "1.0.0", "kind": "module", "name": "Módulo {{id}}", "author": "Pruebas",
              "license": "GPL-3.0-only", "apps": ["rodaje"],
              "module": { "entry": "modulo.exe", "protocol": 1, "functions": ["eco"], "vramMb": {{vram}} } }
            """);
    }
}
