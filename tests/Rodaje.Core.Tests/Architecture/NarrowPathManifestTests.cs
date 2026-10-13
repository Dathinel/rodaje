using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Rodaje.Core.Tests.Architecture;

/// <summary>
/// Prueba de arquitectura de la tarea 234: todo ejecutable de <c>src/</c> y <c>modules/</c> que pasa rutas a código
/// nativo como texto estrecho (<c>char*</c>) declara <c>&lt;activeCodePage&gt;UTF-8&lt;/activeCodePage&gt;</c> en su
/// manifiesto. Sin él, Windows traduce la ruta a la página ANSI del sistema y una carpeta con «ñ», tildes o caracteres
/// de otro alfabeto no llega entera: el modelo no se abre. Son los que usan Whisper.net (whisper.cpp) o LLamaSharp
/// (llama.cpp), o los que declaran sus propias funciones nativas con cadenas UTF-8 o ANSI. Lee los archivos, no compila
/// nada: corre en Linux.
/// </summary>
public sealed class NarrowPathManifestTests
{
    private static readonly XNamespace MsBuild = "http://schemas.microsoft.com/developer/msbuild/2003";
    private static readonly XNamespace AsmV3 = "urn:schemas-microsoft-com:asm.v3";
    private static readonly XNamespace WindowsSettings2019 = "http://schemas.microsoft.com/SMI/2019/WindowsSettings";

    // Paquetes cuya biblioteca nativa recibe la ruta del modelo como char* y la abre con fopen o std::ifstream.
    private static readonly string[] NarrowPathPackages = ["Whisper.net", "LLamaSharp"];

    // Funciones nativas propias con cadenas de un byte por carácter.
    private static readonly Regex NarrowMarshalling = new(
        @"StringMarshalling\s*=\s*StringMarshalling\.Utf8|UnmanagedType\.(LPStr|LPUTF8Str)\b|CharSet\s*=\s*CharSet\.(Ansi|None)\b",
        RegexOptions.CultureInvariant);

    [Fact]
    public void Every_executable_with_narrow_native_paths_declares_utf8()
    {
        string root = AsyncSafetyTests.RepoRoot();
        List<(string Project, string Reason)> narrow = NarrowPathExecutables(root);
        var problems = new List<string>();

        foreach ((string project, string reason) in narrow)
        {
            string relative = Path.GetRelativePath(root, project).Replace('\\', '/');
            string? manifest = Property(project, "ApplicationManifest");
            string? path = manifest is null ? null : Path.Combine(Path.GetDirectoryName(project)!, manifest.Replace('\\', Path.DirectorySeparatorChar));
            if (path is null || !File.Exists(path))
            {
                problems.Add($"{relative}: {reason} y no tiene manifiesto con activeCodePage UTF-8.");
            }
            else if (!DeclaresUtf8(File.ReadAllText(path)))
            {
                problems.Add($"{relative}: {reason} y «{manifest}» no declara <activeCodePage xmlns=\"{WindowsSettings2019}\">UTF-8</activeCodePage> en <application><windowsSettings>.");
            }
        }

        // Subtítulos (Whisper.net) y Traducir (LLamaSharp) tienen que salir: si la búsqueda deja de verlos, no comprobaría nada.
        string[] names = [.. narrow.Select(n => Path.GetFileNameWithoutExtension(n.Project))];
        Assert.Contains("Rodaje.Module.Subtitles", names);
        Assert.Contains("Rodaje.Module.Translate", names);
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void The_check_rejects_a_manifest_without_utf8()
    {
        const string withoutIt = """
            <?xml version="1.0" encoding="utf-8"?>
            <assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
              <application xmlns="urn:schemas-microsoft-com:asm.v3">
                <windowsSettings>
                  <longPathAware xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">true</longPathAware>
                </windowsSettings>
              </application>
            </assembly>
            """;
        const string wrongNamespace = """
            <assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
              <application xmlns="urn:schemas-microsoft-com:asm.v3">
                <windowsSettings>
                  <activeCodePage xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">UTF-8</activeCodePage>
                </windowsSettings>
              </application>
            </assembly>
            """;
        const string legacy = """
            <assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
              <application xmlns="urn:schemas-microsoft-com:asm.v3">
                <windowsSettings>
                  <activeCodePage xmlns="http://schemas.microsoft.com/SMI/2019/WindowsSettings">Legacy</activeCodePage>
                </windowsSettings>
              </application>
            </assembly>
            """;
        const string withIt = """
            <assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
              <application xmlns="urn:schemas-microsoft-com:asm.v3">
                <windowsSettings>
                  <activeCodePage xmlns="http://schemas.microsoft.com/SMI/2019/WindowsSettings"> utf-8 </activeCodePage>
                </windowsSettings>
              </application>
            </assembly>
            """;

        Assert.False(DeclaresUtf8(withoutIt));
        Assert.False(DeclaresUtf8(wrongNamespace));
        Assert.False(DeclaresUtf8(legacy));
        Assert.True(DeclaresUtf8(withIt));
    }

    [Fact]
    public void Own_narrow_imports_count_and_utf16_ones_do_not()
    {
        Assert.Matches(NarrowMarshalling, "[LibraryImport(\"x\", StringMarshalling = StringMarshalling.Utf8)]");
        Assert.Matches(NarrowMarshalling, "[MarshalAs(UnmanagedType.LPStr)] string path");
        Assert.Matches(NarrowMarshalling, "[MarshalAs(UnmanagedType.LPUTF8Str)] string path");
        Assert.Matches(NarrowMarshalling, "[DllImport(\"x\", CharSet = CharSet.Ansi)]");
        Assert.DoesNotMatch(NarrowMarshalling, "[LibraryImport(\"kernel32.dll\", StringMarshalling = StringMarshalling.Utf16)]");
        Assert.DoesNotMatch(NarrowMarshalling, "[MarshalAs(UnmanagedType.LPWStr)] string path");
    }

    internal static bool DeclaresUtf8(string manifest)
    {
        XDocument document = XDocument.Parse(manifest);
        return document.Root!
            .Elements(AsmV3 + "application")
            .Elements(AsmV3 + "windowsSettings")
            .Elements(WindowsSettings2019 + "activeCodePage")
            .Any(e => string.Equals(e.Value.Trim(), "UTF-8", StringComparison.OrdinalIgnoreCase));
    }

    private static List<(string Project, string Reason)> NarrowPathExecutables(string root)
    {
        var found = new List<(string, string)>();
        foreach (string folder in new[] { "src", "modules" })
        {
            foreach (string project in Directory.EnumerateFiles(Path.Combine(root, folder), "*.csproj", SearchOption.AllDirectories))
            {
                string? outputType = Property(project, "OutputType");
                if (!string.Equals(outputType, "Exe", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(outputType, "WinExe", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (Reason(project) is { } reason)
                {
                    found.Add((project, reason));
                }
            }
        }

        found.Sort((a, b) => string.CompareOrdinal(a.Item1, b.Item1));
        return found;
    }

    private static string? Reason(string project)
    {
        XDocument document = XDocument.Load(project);
        foreach (string package in document.Descendants()
            .Where(e => e.Name.LocalName == "PackageReference")
            .Select(e => (string?)e.Attribute("Include") ?? ""))
        {
            if (NarrowPathPackages.Contains(package, StringComparer.OrdinalIgnoreCase))
            {
                return $"usa {package}, que pasa rutas a nativo como texto estrecho,";
            }
        }

        string directory = Path.GetDirectoryName(project)!;
        foreach (string source in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(directory, source).Replace('\\', '/');
            if (relative.StartsWith("bin/", StringComparison.Ordinal) || relative.StartsWith("obj/", StringComparison.Ordinal))
            {
                continue;
            }

            if (NarrowMarshalling.IsMatch(File.ReadAllText(source)))
            {
                return $"declara funciones nativas con cadenas estrechas ({relative})";
            }
        }

        return null;
    }

    private static string? Property(string project, string name)
    {
        XDocument document = XDocument.Load(project);
        return document.Descendants()
            .Where(e => e.Name.LocalName == name && (e.Name.Namespace == XNamespace.None || e.Name.Namespace == MsBuild))
            .Select(e => e.Value.Trim())
            .LastOrDefault(v => v.Length > 0);
    }
}
