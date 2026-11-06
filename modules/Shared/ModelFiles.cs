using System.Security.Cryptography;
using System.Text.Json;
using Rodaje.Modules.Protocol;

namespace Rodaje.Module.Shared;

internal static class ModelFiles
{
    public static string Verify(string path, string file)
    {
        using Stream manifest = typeof(ModelFiles).Assembly.GetManifestResourceStream("Rodaje.Module.Models")
            ?? throw new ModuleCallException(ErrorCodes.InvalidParameters, "Falta la lista de modelos del módulo. Reinstala el módulo.");
        using JsonDocument document = JsonDocument.Parse(manifest);
        foreach (JsonElement model in document.RootElement.GetProperty("module").GetProperty("models").EnumerateArray())
        {
            if (!string.Equals(Path.GetFileName(model.GetProperty("file").GetString()), file, StringComparison.Ordinal))
            {
                continue;
            }

            using FileStream stream = File.OpenRead(path);
            if (stream.Length == model.GetProperty("size").GetInt64()
                && string.Equals(Convert.ToHexStringLower(SHA256.HashData(stream)), model.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase))
            {
                return path;
            }

            throw new ModuleCallException(ErrorCodes.InvalidParameters, $"El modelo {path} está dañado: su tamaño o huella SHA-256 no coincide. Reinstala el módulo.");
        }

        throw new ModuleCallException(ErrorCodes.InvalidParameters, $"El modelo {file} no está declarado en la lista de modelos del módulo.");
    }
}
