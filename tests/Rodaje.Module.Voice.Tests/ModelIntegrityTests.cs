using System.Text.Json;
using Rodaje.Modules.Protocol;

namespace Rodaje.Module.Voice.Tests;

public sealed class ModelIntegrityTests
{
    [Fact]
    public void A_model_with_the_declared_size_but_another_hash_is_rejected()
    {
        string folder = Directory.CreateTempSubdirectory("rodaje-model-integrity-").FullName;
        try
        {
            string path = Path.Combine(folder, Voices.ModelFile);
            using Stream manifest = typeof(Voices).Assembly.GetManifestResourceStream("Rodaje.Module.Models")!;
            using JsonDocument document = JsonDocument.Parse(manifest);
            long size = document.RootElement.GetProperty("module").GetProperty("models").EnumerateArray()
                .Single(m => Path.GetFileName(m.GetProperty("file").GetString()) == Voices.ModelFile).GetProperty("size").GetInt64();
            using (FileStream stream = File.Create(path)) stream.SetLength(size);

            ModuleCallException error = Assert.Throws<ModuleCallException>(() => Voices.FindRoot(folder));

            Assert.Contains("huella SHA-256", error.Message, StringComparison.Ordinal);
            Assert.Throws<ModuleCallException>(() => new Synthesis.KokoroEngine(folder));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
