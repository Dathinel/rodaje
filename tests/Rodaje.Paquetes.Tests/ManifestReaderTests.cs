namespace Rodaje.Paquetes.Tests;

public sealed class ManifestReaderTests
{
    [Fact]
    public void Reads_a_complete_manifest()
    {
        ManifestResult result = ManifestReader.Read(Samples.Manifest(extra: ",\n\"description\": \"Para probar\""));

        Assert.True(result.IsValid, string.Join(" ", result.Errors));
        PackageManifest m = result.Manifest!;
        Assert.Equal("rodaje-transiciones", m.Id);
        Assert.Equal(PackageVersion.Parse("1.0.0"), m.Version);
        Assert.Equal(PackageKind.Content, m.Kind);
        Assert.Equal("Dathinel", m.Author);
        Assert.Equal("MIT", m.License);
        Assert.Equal("Para probar", m.Description);
        Assert.Equal(new Uri("https://github.com/Dathinel/rodaje-transiciones"), m.Repository);
        Assert.True(m.WorksIn("RODAJE"));
        PackageItem item = Assert.Single(m.Items);
        Assert.Equal(new PackageItem("deslizar", ItemKind.Transition, "Deslizar", "transiciones/deslizar.json"), item);
        Assert.Equal("rodaje-transiciones@1.0.0/deslizar", m.ReferenceTo(item).ToString());
    }

    [Fact]
    public void Kind_defaults_to_content_and_comments_and_trailing_commas_are_accepted()
    {
        string json = """
            {
              // comentario
              "formatVersion": 1, "id": "a", "version": "0.1.0", "name": "A", "author": "x", "license": "MIT", "apps": ["rodaje"],
              "items": [{ "id": "t", "kind": "text-style", "name": "T" },],
            }
            """;
        ManifestResult result = ManifestReader.Read(json);

        Assert.True(result.IsValid, string.Join(" ", result.Errors));
        Assert.Equal(PackageKind.Content, result.Manifest!.Kind);
        Assert.Equal(ItemKind.TextStyle, result.Manifest.Items[0].Kind);
    }

    [Theory]
    [InlineData("transition", ItemKind.Transition)]
    [InlineData("text-style", ItemKind.TextStyle)]
    [InlineData("effect", ItemKind.Effect)]
    [InlineData("filter", ItemKind.Filter)]
    [InlineData("lut", ItemKind.Lut)]
    [InlineData("template", ItemKind.Template)]
    [InlineData("sound", ItemKind.Sound)]
    public void Every_item_kind_round_trips(string text, ItemKind kind)
    {
        Assert.Equal(kind, ManifestReader.ParseItemKind(text));
        Assert.Equal(text, ManifestReader.FormatItemKind(kind));
    }

    [Theory]
    [InlineData("""{ "formatVersion": 1 }""", "Falta «id»")]
    [InlineData("[]", "debe ser un objeto")]
    [InlineData("{ nope", "no es JSON válido")]
    public void Broken_manifests_explain_why(string json, string expected)
    {
        ManifestResult result = ManifestReader.Read(json);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains(expected, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Rodaje", "no es válido")]
    [InlineData("a--b", "no es válido")]
    [InlineData("-a", "no es válido")]
    [InlineData("a b", "no es válido")]
    public void Invalid_ids_are_rejected(string id, string expected)
    {
        ManifestResult result = ManifestReader.Read(Samples.Manifest(id: id));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains(expected, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("01.0.0")]
    [InlineData("v1.0.0")]
    [InlineData("1.0.0-")]
    public void Invalid_versions_are_rejected(string version) =>
        Assert.False(ManifestReader.Read(Samples.Manifest(version: version)).IsValid);

    [Fact]
    public void A_newer_format_asks_to_update_the_program()
    {
        ManifestResult result = ManifestReader.Read(Samples.Manifest().Replace("\"formatVersion\": 1", "\"formatVersion\": 2", StringComparison.Ordinal));

        Assert.False(result.IsValid);
        Assert.Contains("actualiza el programa", Assert.Single(result.Errors), StringComparison.Ordinal);
    }

    [Fact]
    public void Repository_must_be_https()
    {
        ManifestResult result = ManifestReader.Read(Samples.Manifest().Replace("https://github.com", "http://github.com", StringComparison.Ordinal));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("https", StringComparison.Ordinal));
    }

    [Fact]
    public void Apps_cannot_be_empty()
    {
        ManifestResult result = ManifestReader.Read(Samples.Manifest(apps: "[]"));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("al menos un programa", StringComparison.Ordinal));
    }

    [Fact]
    public void Unknown_item_kinds_are_skipped_with_a_warning()
    {
        ManifestResult result = ManifestReader.Read(Samples.Manifest(items: """
            [{ "id": "a", "kind": "sticker-3d", "name": "A" }, { "id": "b", "kind": "sound", "name": "B", "file": "b.wav" }]
            """));

        Assert.True(result.IsValid);
        Assert.Equal("b", Assert.Single(result.Manifest!.Items).Id);
        Assert.Contains(result.Warnings, w => w.Contains("sticker-3d", StringComparison.Ordinal));
    }

    [Fact]
    public void Repeated_item_ids_are_an_error()
    {
        ManifestResult result = ManifestReader.Read(Samples.Manifest(items: """
            [{ "id": "a", "kind": "sound", "name": "A" }, { "id": "a", "kind": "sound", "name": "B" }]
            """));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("repetido", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("../fuera.json")]
    [InlineData("/raiz.json")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData("a/../../b.json")]
    [InlineData("datos:flujo")]
    public void Item_files_must_stay_inside_the_package(string file)
    {
        ManifestResult result = ManifestReader.Read(Samples.Manifest(items: $$"""[{ "id": "a", "kind": "lut", "name": "A", "file": "{{file}}" }]"""));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("no es relativa", StringComparison.Ordinal));
    }

    [Fact]
    public void With_a_folder_the_item_files_must_exist()
    {
        using var folder = new TempFolder();
        ManifestResult missing = ManifestReader.Read(Samples.Manifest(), folder.Path);
        Directory.CreateDirectory(folder.Combine("transiciones"));
        File.WriteAllText(folder.Combine("transiciones", "deslizar.json"), "{}");
        ManifestResult present = ManifestReader.Read(Samples.Manifest(), folder.Path);

        Assert.Contains(missing.Errors, e => e.Contains("falta el archivo", StringComparison.Ordinal));
        Assert.True(present.IsValid);
    }

    [Fact]
    public void A_module_declares_its_entry_and_runs_nothing()
    {
        string json = Samples.Manifest(id: "modulo-subtitulos", kind: "module", items: "[]",
            extra: ",\n\"module\": { \"entry\": \"bin/modulo.exe\", \"protocol\": 1, \"functions\": [\"subtitulos\"] }");
        ManifestResult result = ManifestReader.Read(json);

        Assert.True(result.IsValid, string.Join(" ", result.Errors));
        Assert.Equal(PackageKind.Module, result.Manifest!.Kind);
        Assert.Equal(new ModuleInfo("bin/modulo.exe", 1, ["subtitulos"]).Entry, result.Manifest.Module!.Entry);
        Assert.Equal(["subtitulos"], result.Manifest.Module.Functions);
    }

    [Fact]
    public void A_module_without_entry_is_invalid_and_items_cannot_be_modules()
    {
        Assert.False(ManifestReader.Read(Samples.Manifest(kind: "module", items: "[]")).IsValid);
        Assert.False(ManifestReader.Read(Samples.Manifest(items: """[{ "id": "m", "kind": "module", "name": "M" }]""")).IsValid);
        Assert.False(ManifestReader.Read(Samples.Manifest(kind: "plugin")).IsValid);
    }

    [Fact]
    public void Manifest_files_over_one_megabyte_are_refused()
    {
        using var folder = new TempFolder();
        string path = folder.Combine("paquete.json");
        File.WriteAllText(path, new string(' ', ManifestReader.MaxManifestBytes + 1));

        Assert.Contains("1 MB", Assert.Single(ManifestReader.ReadFile(path).Errors), StringComparison.Ordinal);
    }
}
