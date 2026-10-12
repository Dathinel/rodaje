using Rodaje.Packages.Download;

namespace Rodaje.Packages.Tests;

/// <summary>
/// <c>module.models</c>: cada modelo con tamaño, SHA-256 y una dirección fijada, que es lo que necesitan el gestor de
/// complementos (<see cref="ModelStore"/>) y la distribución completa; y la búsqueda del modelo ya instalado.
/// </summary>
public sealed class ModuleModelsTests
{
    private const string Sha = "3605803b982cb64aead44f6c1b2ae36e3acdb41d8e46c8a94c6533bc4c67e597";
    private const string Commit = "5359861c739e955e79d9a303bcbc70fb988958b1";
    private const string PinnedUrl = "https://huggingface.co/org/modelo/resolve/" + Commit + "/modelo.gguf";

    private static string Model(string? size = "123", string url = PinnedUrl, string sha = Sha, string id = "modelo", string file = "modelos/modelo.gguf")
    {
        string sizeField = size is null ? "" : $"\"size\": {size}, ";
        return $$"""{ "id": "{{id}}", "file": "{{file}}", {{sizeField}}"sha256": "{{sha}}", "license": "Apache-2.0", "url": "{{url}}" }""";
    }

    private static string Manifest(params string[] models) => Samples.Manifest(id: "modulo-prueba", kind: "module", items: "[]",
        extra: ",\n\"module\": { \"entry\": \"bin/modulo.exe\", \"protocol\": 1, \"functions\": [\"f\"], \"models\": [" + string.Join(", ", models) + "] }");

    [Fact]
    public void A_model_with_size_hash_and_a_pinned_url_is_valid()
    {
        ManifestResult result = ManifestReader.Read(Manifest(Model()));
        Assert.True(result.IsValid, string.Join(" ", result.Errors));
    }

    [Fact]
    public void A_module_without_models_is_still_valid()
    {
        Assert.True(ManifestReader.Read(Samples.Manifest(id: "modulo-prueba", kind: "module", items: "[]",
            extra: ",\n\"module\": { \"entry\": \"bin/modulo.exe\", \"protocol\": 1, \"functions\": [\"f\"] }")).IsValid);
        Assert.True(ManifestReader.Read(Manifest()).IsValid);
    }

    [Theory]
    [InlineData(null, "size")]
    [InlineData("0", "size")]
    [InlineData("-5", "size")]
    [InlineData("1.5", "size")]
    [InlineData("\"123\"", "size")]
    public void A_model_needs_its_exact_size(string? size, string expected)
    {
        ManifestResult result = ManifestReader.Read(Manifest(Model(size: size)));
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains(expected, StringComparison.Ordinal) && e.Contains("«modelo»", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("https://huggingface.co/org/modelo/resolve/main/modelo.gguf")]
    [InlineData("https://huggingface.co/org/modelo/resolve/5359861c/modelo.gguf")]
    [InlineData("https://huggingface.co/org/modelo/resolve/5359861C739E955E79D9A303BCBC70FB988958B1/modelo.gguf")]
    [InlineData("https://huggingface.co/org/modelo/blob/5359861c739e955e79d9a303bcbc70fb988958b1/modelo.gguf")]
    [InlineData("http://huggingface.co/org/modelo/resolve/5359861c739e955e79d9a303bcbc70fb988958b1/modelo.gguf")]
    [InlineData("https://huggingface.co/org/modelo/resolve/5359861c739e955e79d9a303bcbc70fb988958b1/modelo.gguf?download=true")]
    [InlineData("https://github.com/org/repo/raw/main/modelo.onnx")]
    [InlineData("https://github.com/org/repo/blob/5359861c739e955e79d9a303bcbc70fb988958b1/modelo.onnx")]
    [InlineData("https://raw.githubusercontent.com/org/repo/main/modelo.json")]
    [InlineData("https://ejemplo.com/5359861c739e955e79d9a303bcbc70fb988958b1/modelo.gguf")]
    [InlineData("no es una url")]
    public void A_model_url_must_be_pinned(string url)
    {
        Assert.False(ManifestReader.IsPinnedModelUrl(url, out _));
        ManifestResult result = ManifestReader.Read(Manifest(Model(url: url)));
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("no está fijada", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(PinnedUrl)]
    [InlineData("https://github.com/snakers4/silero-vad/raw/5cd7945676eb32225748052e2e6a0580e4686a08/src/silero_vad/data/silero_vad.onnx")]
    [InlineData("https://raw.githubusercontent.com/hexgrad/misaki/fba1236595f2d2bf21d414ba6e57d25256afada3/misaki/data/us_gold.json")]
    [InlineData("https://github.com/Dathinel/rodaje-modelos/releases/download/v1/modelo.gguf")]
    public void Pinned_urls_are_accepted(string url)
    {
        Assert.True(ManifestReader.IsPinnedModelUrl(url, out Uri? parsed));
        Assert.Equal(url, parsed!.OriginalString);
    }

    [Fact]
    public void Every_hugging_face_url_the_reader_accepts_is_one_the_store_can_download()
    {
        Assert.True(ManifestReader.IsPinnedModelUrl(PinnedUrl, out Uri? url));
        Assert.True(DownloadUrlPolicy.Default.IsAllowed(url!, out _));
    }

    [Theory]
    [InlineData("ABCDEF")]
    [InlineData("3605803B982CB64AEAD44F6C1B2AE36E3ACDB41D8E46C8A94C6533BC4C67E597")]
    [InlineData("3605803b982cb64aead44f6c1b2ae36e3acdb41d8e46c8a94c6533bc4c67e59")]
    public void A_model_needs_a_lowercase_sha256(string sha)
    {
        ManifestResult result = ManifestReader.Read(Manifest(Model(sha: sha)));
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("sha256", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("../fuera.gguf")]
    [InlineData("/modelos/abs.gguf")]
    [InlineData("C:/modelos/abs.gguf")]
    public void A_model_file_stays_inside_the_package(string file)
    {
        Assert.False(ManifestReader.Read(Manifest(Model(file: file))).IsValid);
    }

    [Fact]
    public void Models_must_be_a_list_of_objects_with_unique_ids()
    {
        Assert.False(ManifestReader.Read(Samples.Manifest(id: "modulo-prueba", kind: "module", items: "[]",
            extra: ",\n\"module\": { \"entry\": \"bin/modulo.exe\", \"protocol\": 1, \"functions\": [\"f\"], \"models\": {} }")).IsValid);
        Assert.False(ManifestReader.Read(Manifest("3")).IsValid);
        ManifestResult repeated = ManifestReader.Read(Manifest(Model(), Model()));
        Assert.False(repeated.IsValid);
        Assert.Contains(repeated.Errors, e => e.Contains("repetido", StringComparison.Ordinal));
    }

    [Fact]
    public void ReadModels_returns_the_validated_models_or_null()
    {
        using var temp = new TempFolder();
        string path = temp.Combine(PackageManifest.FileName);
        File.WriteAllText(path, Manifest(Model(size: "2500000000")));
        ModuleModel model = Assert.Single(ManifestReader.ReadModels(path)!);
        Assert.Equal(("modelo", "modelos/modelo.gguf", Sha, 2_500_000_000L, "Apache-2.0"), (model.Id, model.File, model.Sha256, model.Size, model.License));
        Assert.Equal(new ModelSpec("modelo", new Uri(PinnedUrl), Sha, 2_500_000_000L), model.Spec);

        File.WriteAllText(path, Manifest(Model(size: null)));
        Assert.Null(ManifestReader.ReadModels(path));
        File.WriteAllText(path, Samples.Manifest());
        Assert.Null(ManifestReader.ReadModels(path));
        Assert.Null(ManifestReader.ReadModels(temp.Combine("no-existe.json")));
    }

    [Fact]
    public void Find_looks_in_the_store_by_hash_and_in_the_module_folder_by_name_with_the_exact_size()
    {
        using var temp = new TempFolder();
        string store = temp.Combine("complementos", "modelos");
        string bundled = temp.Combine("app", "modelos");
        string moduleDir = temp.Combine("complementos", "modulos", "modulo-prueba");
        Directory.CreateDirectory(store);
        Directory.CreateDirectory(bundled);
        Directory.CreateDirectory(Path.Combine(moduleDir, "modelos"));
        using var http = new HttpClient();
        var modelStore = new ModelStore(store, new FileDownloader(http), bundledFolders: [bundled]);
        var model = new ModuleModel("modelo", "modelos/modelo.gguf", Sha, 4, "Apache-2.0", new Uri(PinnedUrl));

        Assert.Null(model.Find(modelStore, moduleDir));

        // Por nombre en la carpeta del módulo, solo con el tamaño exacto.
        string named = Path.Combine(moduleDir, "modelos", "modelo.gguf");
        File.WriteAllBytes(named, [1, 2, 3]);
        Assert.Null(model.Find(modelStore, moduleDir));
        File.WriteAllBytes(named, [1, 2, 3, 4]);
        Assert.Equal(Path.GetFullPath(named), model.Find(modelStore, moduleDir));
        Assert.Null(model.Find(modelStore, null));
        File.Delete(named);

        // Por hash en la distribución completa y, antes, en el almacén.
        File.WriteAllBytes(Path.Combine(bundled, Sha), [1, 2, 3, 4]);
        Assert.Equal(Path.Combine(bundled, Sha), model.Find(modelStore, moduleDir));
        File.WriteAllBytes(Path.Combine(store, Sha), [1, 2, 3, 4]);
        Assert.Equal(Path.Combine(store, Sha), model.Find(modelStore, moduleDir));

        // Un archivo con el nombre de otro hash no cuenta.
        File.Delete(Path.Combine(store, Sha));
        File.Delete(Path.Combine(bundled, Sha));
        File.WriteAllBytes(Path.Combine(store, new string('a', 64)), [1, 2, 3, 4]);
        Assert.Null(model.Find(modelStore, moduleDir));
    }

    [Fact]
    public void A_model_file_outside_the_module_folder_is_not_found()
    {
        using var temp = new TempFolder();
        string moduleDir = temp.Combine("modulo");
        Directory.CreateDirectory(moduleDir);
        File.WriteAllBytes(temp.Combine("fuera.gguf"), [1, 2, 3, 4]);
        using var http = new HttpClient();
        var store = new ModelStore(temp.Combine("almacen"), new FileDownloader(http));
        var model = new ModuleModel("modelo", "../fuera.gguf", Sha, 4, "Apache-2.0", new Uri(PinnedUrl));
        Assert.Null(model.Find(store, moduleDir));
    }

    /// <summary>Los manifiestos de los módulos del repositorio siguen siendo válidos con la validación de los modelos.</summary>
    [Fact]
    public void The_repository_module_manifests_declare_valid_models()
    {
        string? modules = null;
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null && modules is null; d = d.Parent)
        {
            string candidate = Path.Combine(d.FullName, "modules");
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(d.FullName, "Rodaje.slnx")))
            {
                modules = candidate;
            }
        }

        Assert.NotNull(modules);
        string[] manifests = Directory.GetFiles(modules, PackageManifest.FileName, SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToArray();
        Assert.NotEmpty(manifests);
        int withModels = 0;
        foreach (string manifest in manifests)
        {
            ManifestResult result = ManifestReader.ReadFile(manifest, checkFiles: false);
            Assert.True(result.IsValid, manifest + ": " + string.Join(" ", result.Errors));
            IReadOnlyList<ModuleModel>? models = ManifestReader.ReadModels(manifest);
            Assert.NotNull(models);
            withModels += models.Count > 0 ? 1 : 0;
        }

        Assert.True(withModels >= 5, $"Solo {withModels} módulos declaran modelos.");
    }
}
