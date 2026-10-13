using System.Xml.Linq;

namespace Rodaje.Core.Tests.Architecture;

/// <summary>
/// Prueba de arquitectura sobre los proyectos ejecutables de <c>src/</c> y <c>modules/</c> (tarea 233, #225): cada
/// ejecutable que publica Rodaje (la app, el lanzador y los módulos, que son procesos aparte y abren medios y modelos)
/// lleva un <c>ApplicationManifest</c> que declara <c>&lt;longPathAware&gt;true&lt;/longPathAware&gt;</c> en
/// <c>&lt;application&gt;&lt;windowsSettings&gt;</c>. Sin él, aunque Windows tenga <c>LongPathsEnabled</c>, las llamadas
/// nativas (WinUI, ONNX Runtime, whisper.cpp, llama.cpp…) siguen limitadas a 260 caracteres. Lee los archivos, no
/// compila nada: corre en Linux.
/// </summary>
public sealed class LongPathManifestTests
{
    private static readonly XNamespace MsBuild = "http://schemas.microsoft.com/developer/msbuild/2003";
    private static readonly XNamespace AsmV3 = "urn:schemas-microsoft-com:asm.v3";
    private static readonly XNamespace WindowsSettings2016 = "http://schemas.microsoft.com/SMI/2016/WindowsSettings";

    [Fact]
    public void Every_published_executable_declares_longPathAware()
    {
        string root = AsyncSafetyTests.RepoRoot();
        List<string> projects = ExecutableProjects(root);
        var problems = new List<string>();

        foreach (string project in projects)
        {
            string relative = Path.GetRelativePath(root, project).Replace('\\', '/');
            string? manifest = ManifestOf(project);
            if (manifest is null)
            {
                problems.Add($"{relative}: es un ejecutable y no declara <ApplicationManifest> (hace falta uno con longPathAware).");
                continue;
            }

            string path = Path.Combine(Path.GetDirectoryName(project)!, manifest.Replace('\\', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
            {
                problems.Add($"{relative}: el manifiesto «{manifest}» no existe.");
                continue;
            }

            if (!DeclaresLongPathAware(File.ReadAllText(path)))
            {
                problems.Add($"{relative}: «{manifest}» no declara <longPathAware xmlns=\"{WindowsSettings2016}\">true</longPathAware> en <application><windowsSettings>.");
            }
        }

        // La app, el lanzador y los siete módulos: si la búsqueda deja de encontrarlos, la prueba no comprobaría nada.
        Assert.True(projects.Count >= 9, $"Se esperaban al menos 9 proyectos ejecutables en src/ y modules/; se encontraron {projects.Count}.");
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void The_check_rejects_a_manifest_without_longPathAware()
    {
        const string withoutIt = """
            <?xml version="1.0" encoding="utf-8"?>
            <assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
              <application xmlns="urn:schemas-microsoft-com:asm.v3">
                <windowsSettings>
                  <dpiAwareness xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">PerMonitorV2</dpiAwareness>
                </windowsSettings>
              </application>
            </assembly>
            """;
        const string wrongNamespace = """
            <assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
              <application xmlns="urn:schemas-microsoft-com:asm.v3">
                <windowsSettings>
                  <longPathAware xmlns="http://schemas.microsoft.com/SMI/2005/WindowsSettings">true</longPathAware>
                </windowsSettings>
              </application>
            </assembly>
            """;
        const string disabled = """
            <assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
              <application xmlns="urn:schemas-microsoft-com:asm.v3">
                <windowsSettings>
                  <longPathAware xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">false</longPathAware>
                </windowsSettings>
              </application>
            </assembly>
            """;
        const string withIt = """
            <assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
              <application xmlns="urn:schemas-microsoft-com:asm.v3">
                <windowsSettings>
                  <longPathAware xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings"> true </longPathAware>
                </windowsSettings>
              </application>
            </assembly>
            """;

        Assert.False(DeclaresLongPathAware(withoutIt));
        Assert.False(DeclaresLongPathAware(wrongNamespace));
        Assert.False(DeclaresLongPathAware(disabled));
        Assert.True(DeclaresLongPathAware(withIt));
    }

    internal static bool DeclaresLongPathAware(string manifest)
    {
        XDocument document = XDocument.Parse(manifest);
        return document.Root!
            .Elements(AsmV3 + "application")
            .Elements(AsmV3 + "windowsSettings")
            .Elements(WindowsSettings2016 + "longPathAware")
            .Any(e => string.Equals(e.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase));
    }

    private static List<string> ExecutableProjects(string root)
    {
        var projects = new List<string>();
        foreach (string folder in new[] { "src", "modules" })
        {
            foreach (string project in Directory.EnumerateFiles(Path.Combine(root, folder), "*.csproj", SearchOption.AllDirectories))
            {
                string? outputType = Property(project, "OutputType");
                if (string.Equals(outputType, "Exe", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(outputType, "WinExe", StringComparison.OrdinalIgnoreCase))
                {
                    projects.Add(project);
                }
            }
        }

        projects.Sort(StringComparer.Ordinal);
        return projects;
    }

    private static string? ManifestOf(string project) => Property(project, "ApplicationManifest");

    private static string? Property(string project, string name)
    {
        XDocument document = XDocument.Load(project);
        return document.Descendants()
            .Where(e => e.Name.LocalName == name && (e.Name.Namespace == XNamespace.None || e.Name.Namespace == MsBuild))
            .Select(e => e.Value.Trim())
            .LastOrDefault(v => v.Length > 0);
    }
}
