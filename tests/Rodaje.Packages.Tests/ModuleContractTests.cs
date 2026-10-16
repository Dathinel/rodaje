using Rodaje.Packages.Download;

namespace Rodaje.Packages.Tests;

/// <summary>
/// Contrato de la sección «Módulos de IA» de <c>docs/formato-paquete.md</c>: una prueba por promesa, con la frase del
/// documento encima.
/// </summary>
public sealed class ModuleContractTests : IDisposable
{
    private const string Sha = "3605803b982cb64aead44f6c1b2ae36e3acdb41d8e46c8a94c6533bc4c67e597";
    private const string Url = "https://huggingface.co/org/modelo/resolve/0123456789abcdef0123456789abcdef01234567/modelo.gguf";
    private const string Entry = "Rodaje.Module.Subtitles.exe";

    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private static string Manifest(string module, string kind = "module") =>
        Samples.Manifest(id: "modulo-subtitulos", kind: kind, items: "[]", extra: ",\n\"module\": " + module);

    private static string WithModels(string entry = Entry) =>
        Manifest($$"""
            { "entry": "{{entry}}", "protocol": 1, "functions": ["subtitulos"], "vramMb": 1500,
              "models": [{ "id": "whisper", "file": "modelos/whisper.bin", "size": 123, "sha256": "{{Sha}}", "license": "MIT", "url": "{{Url}}" }] }
            """);

    // «Un módulo es un paquete con `"kind": "module"` y un bloque `module`:
    // "module": { "entry": "Rodaje.Module.Subtitles.exe", "protocol": 1, "functions": ["subtitulos"], "vramMb": 1500 }»
    [Fact]
    public void The_documented_module_block_is_read()
    {
        ManifestResult result = ManifestReader.Read(Manifest(
            """{ "entry": "Rodaje.Module.Subtitles.exe", "protocol": 1, "functions": ["subtitulos"], "vramMb": 1500 }"""));

        Assert.True(result.IsValid, string.Join(" ", result.Errors));
        Assert.Empty(result.Warnings);
        Assert.Equal(PackageKind.Module, result.Manifest!.Kind);
        Assert.Equal(Entry, result.Manifest.Module!.Entry);
        Assert.Equal(1, result.Manifest.Module.Protocol);
        Assert.Equal(["subtitulos"], result.Manifest.Module.Functions);
    }

    // «Un módulo es un paquete con `"kind": "module"` y un bloque `module`»
    [Fact]
    public void A_module_needs_its_module_block()
    {
        Assert.False(ManifestReader.Read(Samples.Manifest(id: "modulo-subtitulos", kind: "module", items: "[]")).IsValid);
        Assert.False(ManifestReader.Read(Samples.Manifest(id: "modulo-subtitulos", kind: "module", items: "[]",
            extra: ",\n\"module\": []")).IsValid);
    }

    // «`entry` va en la raíz del paquete»: al validar con la carpeta, el exe tiene que estar ahí.
    [Fact]
    public void The_entry_at_the_package_root_must_exist()
    {
        string json = WithModels();
        Assert.Contains(ManifestReader.Read(json, _folder.Path).Errors, e => e.Contains("module.entry", StringComparison.Ordinal));

        File.WriteAllText(_folder.Combine(Entry), "MZ");
        ManifestResult result = ManifestReader.Read(json, _folder.Path);
        Assert.True(result.IsValid, string.Join(" ", result.Errors));
    }

    // «el módulo busca sus modelos en `modelos\` junto a su exe, y cada `file` de `models` es `modelos/…`»
    [Fact]
    public void A_model_file_lands_in_modelos_next_to_a_root_entry()
    {
        string module = _folder.Combine("modulos", "modulo-subtitulos");
        ModuleModel model = Assert.Single(ModuleModels.Parse(System.Text.Json.JsonDocument.Parse(WithModels()).RootElement));

        string exeFolder = Path.GetDirectoryName(Path.GetFullPath(Path.Combine(module, Entry)))!;
        Assert.Equal(Path.Combine(exeFolder, "modelos"), Path.GetDirectoryName(model.PathIn(module)));
    }

    // «Se instala en `modulos\<id>\` sin los modelos»
    [Fact]
    public void A_module_installs_into_modulos_id_without_its_models()
    {
        var locations = PackageLocations.Under(_folder.Combine("complementos"));
        string archive = Samples.Zip(_folder.Combine("m.paquete"), ("paquete.json", WithModels()), (Entry, "MZ"));

        InstallResult result = new PackageInstaller(locations, "rodaje").Install(archive);

        string target = Path.Combine(locations.Modules, "modulo-subtitulos");
        Assert.Equal(target, result.Package.Directory);
        Assert.Empty(Directory.GetDirectories(locations.Packages));
        Assert.True(File.Exists(Path.Combine(target, Entry)));
        ModuleModel model = Assert.Single(ModuleModels.Read(Path.Combine(target, PackageManifest.FileName)));
        Assert.False(model.IsPresentIn(target));
        Assert.False(Directory.Exists(Path.Combine(target, "modelos")));
    }

    // «Rodaje los baja después y los deja en `<carpeta del módulo>\<file>`»
    [Fact]
    public void A_model_goes_to_the_module_folder_plus_its_file()
    {
        string module = _folder.Combine("modulos", "modulo-subtitulos");
        var model = new ModuleModel("whisper", "modelos/sub/whisper.bin", 3, Sha, new Uri(Url), "MIT");

        Assert.Equal(Path.Combine(module, "modelos", "sub", "whisper.bin"), model.PathIn(module));
        Assert.False(model.IsPresentIn(module));

        Directory.CreateDirectory(Path.Combine(module, "modelos", "sub"));
        File.WriteAllBytes(model.PathIn(module), [1, 2, 3]);
        Assert.True(model.IsPresentIn(module));
    }

    // «Los campos `vramMb`, `runtime` y `models` se describen en [Empaquetarlo]»: el lector de manifiestos los acepta.
    [Fact]
    public void Vram_runtime_and_models_are_accepted()
    {
        ManifestResult result = ManifestReader.Read(Manifest(
            $$"""{ "entry": "{{Entry}}", "protocol": 1, "functions": ["subtitulos"], "runtime": "vulkan", "vramMb": 1500, "models": [] }"""));

        Assert.True(result.IsValid, string.Join(" ", result.Errors));
        Assert.Empty(result.Warnings);
    }
}
