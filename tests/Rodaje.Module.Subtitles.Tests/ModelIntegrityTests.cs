using System.Text.Json;
using Rodaje.Modules.Protocol;

namespace Rodaje.Module.Subtitles.Tests;

public sealed class ModelIntegrityTests
{
    [Fact]
    public void A_model_with_the_declared_size_but_another_hash_is_rejected()
    {
        string folder = Directory.CreateTempSubdirectory("rodaje-model-integrity-").FullName;
        try
        {
            string path = Path.Combine(folder, Transcriber.ModelFile);
            using Stream manifest = typeof(Transcriber).Assembly.GetManifestResourceStream("Rodaje.Module.Models")!;
            using JsonDocument document = JsonDocument.Parse(manifest);
            long size = document.RootElement.GetProperty("module").GetProperty("models").EnumerateArray()
                .Single(m => Path.GetFileName(m.GetProperty("file").GetString()) == Transcriber.ModelFile).GetProperty("size").GetInt64();
            using (FileStream stream = File.Create(path)) stream.SetLength(size);

            ModuleCallException error = Assert.Throws<ModuleCallException>(() => Transcriber.FindModel(path, Transcriber.ModelFile));

            Assert.Contains("huella SHA-256", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
