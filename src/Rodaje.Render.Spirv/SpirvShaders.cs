using System.Reflection;
using System.Text;

namespace Rodaje.Render.Spirv;

/// <summary>Acceso a los módulos SPIR-V incrustados y a su manifiesto.</summary>
public static class SpirvShaders
{
    private static readonly Assembly s_assembly = typeof(SpirvShaders).Assembly;
    private static readonly Lazy<string> s_manifestJson = new(LoadManifestJson);

    /// <summary>Contenido en formato JSON del manifiesto de variantes SPIR-V incrustado.</summary>
    public static string ManifestJson => s_manifestJson.Value;

    /// <summary>Abre el flujo de lectura del SPIR-V incrustado correspondiente a la variante indicada por su identificador.</summary>
    public static Stream? Open(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        string file = $"{id.Replace(':', '-')}.spv";
        return s_assembly.GetManifestResourceStream(file)
            ?? s_assembly.GetManifestResourceStream($"Rodaje.Render.Spirv.Shaders.{file}");
    }

    private static string LoadManifestJson()
    {
        using Stream? stream = s_assembly.GetManifestResourceStream("manifest.json")
            ?? s_assembly.GetManifestResourceStream("Rodaje.Render.Spirv.Shaders.manifest.json");
        if (stream == null)
        {
            throw new InvalidOperationException("No se encontró manifest.json como recurso incrustado.");
        }

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
