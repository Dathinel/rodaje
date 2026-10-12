using System.IO.Compression;
using System.Text;

namespace Rodaje.Packages.Tests;

/// <summary>Carpeta temporal que se borra al terminar.</summary>
internal sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rodaje-paquetes-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>Manifiestos y zips de prueba.</summary>
internal static class Samples
{
    public static string Manifest(
        string id = "rodaje-transiciones",
        string version = "1.0.0",
        string kind = "content",
        string apps = "[\"rodaje\"]",
        string items = """[{ "id": "deslizar", "kind": "transition", "name": "Deslizar", "file": "transiciones/deslizar.json" }]""",
        string extra = "") =>
        $$"""
        {
          "formatVersion": 1,
          "id": "{{id}}",
          "version": "{{version}}",
          "kind": "{{kind}}",
          "name": "Transiciones de prueba",
          "author": "Dathinel",
          "license": "MIT",
          "repository": "https://github.com/Dathinel/{{id}}",
          "apps": {{apps}},
          "items": {{items}}{{extra}}
        }
        """;

    /// <summary>Un zip con las entradas dadas (nombre tal cual, contenido en UTF-8).</summary>
    public static string Zip(string path, params (string Name, string Content)[] entries)
    {
        using (var stream = new FileStream(path, FileMode.Create))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            foreach ((string name, string content) in entries)
            {
                ZipArchiveEntry entry = zip.CreateEntry(name);
                using Stream s = entry.Open();
                s.Write(Encoding.UTF8.GetBytes(content));
            }
        }

        return path;
    }

    public static string ValidPackage(string path, string id = "rodaje-transiciones", string version = "1.0.0", string apps = "[\"rodaje\"]") =>
        Zip(path, ("paquete.json", Manifest(id, version, apps: apps)), ("transiciones/deslizar.json", "{}"));
}
