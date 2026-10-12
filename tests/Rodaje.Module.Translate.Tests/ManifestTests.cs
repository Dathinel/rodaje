using System.Text.Json;
using Rodaje.Packages;

namespace Rodaje.Module.Translate.Tests;

/// <summary>El <c>paquete.json</c> del módulo lo lee la app como módulo del protocolo 1 con la función <c>traducir</c>.</summary>
public sealed class ManifestTests
{
    private static string ManifestPath()
    {
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            string candidate = Path.Combine(d.FullName, "modules", "Rodaje.Module.Translate", "paquete.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("No encuentro modules/Rodaje.Module.Translate/paquete.json.");
    }

    [Fact]
    public void The_manifest_is_a_protocol_1_module_with_the_translate_function()
    {
        ManifestResult result = ManifestReader.ReadFile(ManifestPath(), checkFiles: false);
        Assert.Empty(result.Errors);
        Assert.Equal(PackageKind.Module, result.Manifest!.Kind);
        Assert.Equal("modulo-traducir", result.Manifest.Id);
        Assert.Equal(1, result.Manifest.Module!.Protocol);
        Assert.Equal(["traducir"], result.Manifest.Module.Functions);
    }

    [Fact]
    public void The_model_entry_names_the_file_the_module_looks_for_with_a_pinned_hash()
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(ManifestPath()));
        JsonElement module = doc.RootElement.GetProperty("module");
        Assert.InRange(module.GetProperty("vramMb").GetInt32(), 1, 4096);
        JsonElement model = Assert.Single(module.GetProperty("models").EnumerateArray());
        Assert.Equal("modelos/" + ModelFiles.ModelFile, model.GetProperty("file").GetString());
        Assert.Matches("^[0-9a-f]{64}$", model.GetProperty("sha256").GetString()!);
        Assert.Equal("Apache-2.0", model.GetProperty("license").GetString());
        Assert.EndsWith("/" + ModelFiles.ModelFile, model.GetProperty("url").GetString(), StringComparison.Ordinal);
    }
}
