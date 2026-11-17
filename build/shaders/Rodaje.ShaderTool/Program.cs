using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Rodaje.Render.Shaders;

namespace Rodaje.ShaderTool;

public static class Program
{
    private const int BShift = 0;
    private const int TShift = 16;
    private const int SShift = 32;
    private const int UShift = 48;

    private static readonly string[] s_commonArguments =
    [
        "-spirv",
        "-fspv-target-env=vulkan1.1",
        "-O3",
        "-fvk-b-shift", $"{BShift}", "0",
        "-fvk-t-shift", $"{TShift}", "0",
        "-fvk-s-shift", $"{SShift}", "0",
        "-fvk-u-shift", $"{UShift}", "0",
    ];

    public static int Main(string[] args)
    {
        try
        {
            string command = args.Length > 0 ? args[0].ToLowerInvariant() : "generar";
            string repoRoot = FindRepoRoot();
            string defaultTarget = Path.Combine(repoRoot, "src", "Rodaje.Render.Spirv", "Shaders");
            string targetDir = args.Length > 1 ? Path.GetFullPath(args[1]) : defaultTarget;

            string dxcPath = FindDxc(repoRoot);

            return command switch
            {
                "generar" => Generar(dxcPath, targetDir),
                "comprobar" => Comprobar(dxcPath, targetDir),
                _ => throw new ArgumentException($"Comando no reconocido: '{command}'. Usa 'generar' o 'comprobar'."),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error en Rodaje.ShaderTool: {ex.Message}");
            return 1;
        }
    }

    private static int Generar(string dxcPath, string targetDir)
    {
        Console.WriteLine($"Generando shaders SPIR-V en: {targetDir}");
        Directory.CreateDirectory(targetDir);

        var stopwatch = Stopwatch.StartNew();
        ManifestModel manifest = CompileAll(dxcPath, targetDir);

        // Limpiar archivos .spv que no correspondan a ninguna variante
        var validFiles = manifest.Variants.Select(v => v.File).ToHashSet(StringComparer.OrdinalIgnoreCase);
        validFiles.Add("manifest.json");

        foreach (string file in Directory.EnumerateFiles(targetDir))
        {
            string name = Path.GetFileName(file);
            if (!validFiles.Contains(name))
            {
                File.Delete(file);
                Console.WriteLine($"Eliminado archivo sobrante: {name}");
            }
        }

        long totalBytes = manifest.Variants.Sum(v => new FileInfo(Path.Combine(targetDir, v.File)).Length);
        stopwatch.Stop();

        Console.WriteLine($"Compilación completada con éxito en {stopwatch.ElapsedMilliseconds} ms.");
        Console.WriteLine($"Compilador: {manifest.Compiler}");
        Console.WriteLine($"Variantes compiladas: {manifest.Variants.Length}");
        Console.WriteLine($"Tamaño total de .spv: {totalBytes:N0} bytes ({(totalBytes / 1024.0):F1} KB)");

        return 0;
    }

    private static int Comprobar(string dxcPath, string targetDir)
    {
        Console.WriteLine($"Comprobando shaders SPIR-V frente a: {targetDir}");
        if (!Directory.Exists(targetDir))
        {
            Console.Error.WriteLine($"El directorio de destino no existe: {targetDir}");
            return 1;
        }

        string tempDir = Path.Combine(Path.GetTempPath(), "rodaje-shaders-check-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempDir);
            ManifestModel manifest = CompileAll(dxcPath, tempDir);

            var expectedFiles = manifest.Variants.Select(v => v.File).ToHashSet(StringComparer.OrdinalIgnoreCase);
            expectedFiles.Add("manifest.json");

            var actualFiles = Directory.GetFiles(targetDir).Select(Path.GetFileName).OfType<string>()
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var differences = new List<string>();

            foreach (string file in expectedFiles)
            {
                string targetFile = Path.Combine(targetDir, file);
                if (!File.Exists(targetFile))
                {
                    differences.Add($"Falta en destino: {file}");
                    continue;
                }

                byte[] tempBytes = File.ReadAllBytes(Path.Combine(tempDir, file));
                byte[] targetBytes = File.ReadAllBytes(targetFile);

                if (!tempBytes.AsSpan().SequenceEqual(targetBytes))
                {
                    differences.Add($"Difiere el contenido de: {file}");
                }
            }

            foreach (string file in actualFiles)
            {
                if (!expectedFiles.Contains(file))
                {
                    differences.Add($"Archivo sobrante en destino: {file}");
                }
            }

            if (differences.Count > 0)
            {
                Console.Error.WriteLine("Los shaders SPIR-V no están al día:");
                foreach (string diff in differences)
                {
                    Console.Error.WriteLine($"  - {diff}");
                }

                return 1;
            }

            Console.WriteLine($"Comprobación correcta: {manifest.Variants.Length} variantes al día.");
            return 0;
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    private static ManifestModel CompileAll(string dxcPath, string outputDir)
    {
        string hlslTempDir = Path.Combine(Path.GetTempPath(), "rodaje-hlsl-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(hlslTempDir);

            string compilerVersion = GetDxcVersion(dxcPath);
            var variants = new VariantEntry[ShaderManifest.All.Count];

            Parallel.For(0, ShaderManifest.All.Count, i =>
            {
                ShaderVariant variant = ShaderManifest.All[i];
                string id = ShaderManifest.Id(variant);
                string fileName = $"{id.Replace(':', '-')}.spv";
                string spvPath = Path.Combine(outputDir, fileName);
                string sourcePath = Path.Combine(hlslTempDir, $"{i:000}-{variant.SourceName}");
                File.WriteAllText(sourcePath, variant.Source, new UTF8Encoding(false));

                string profile = variant.Profile.Replace("5_0", "6_0", StringComparison.Ordinal);

                var args = new List<string>(s_commonArguments)
                {
                    "-T", profile,
                    "-E", variant.EntryPoint,
                };

                foreach (string define in variant.Defines)
                {
                    args.Add("-D");
                    args.Add($"{define}=1");
                }

                args.Add("-Fo");
                args.Add(spvPath);
                args.Add(sourcePath);

                (int code, string log) = RunProcess(dxcPath, args);
                if (code != 0)
                {
                    throw new InvalidOperationException($"Error al compilar variante '{id}' con DXC:\n{log}");
                }

                byte[] spvBytes = File.ReadAllBytes(spvPath);
                string sha256 = Convert.ToHexStringLower(SHA256.HashData(spvBytes));
                List<SpirvReflect.Resource> reflected = SpirvReflect.Read(spvBytes);

                ResourceEntry[] resources = reflected.Select(r => new ResourceEntry
                {
                    Register = GetHlslRegister(r.Binding),
                    Name = r.Name,
                    Set = r.Set,
                    Binding = r.Binding,
                    Kind = r.Kind,
                    Dimension = r.Dimension,
                    Size = r.Size,
                }).ToArray();

                variants[i] = new VariantEntry
                {
                    Id = id,
                    File = fileName,
                    EntryPoint = variant.EntryPoint,
                    Profile = profile,
                    Defines = variant.Defines,
                    Sha256 = sha256,
                    Resources = resources,
                };
            });

            var manifest = new ManifestModel
            {
                Compiler = compilerVersion,
                Arguments = s_commonArguments,
                Variants = variants,
            };

            string manifestJson = JsonSerializer.Serialize(manifest, ManifestJsonContext.Default.ManifestModel);
            // Formato normalizado con LF
            manifestJson = manifestJson.Replace("\r\n", "\n", StringComparison.Ordinal);
            if (!manifestJson.EndsWith('\n'))
            {
                manifestJson += "\n";
            }

            string manifestPath = Path.Combine(outputDir, "manifest.json");
            File.WriteAllText(manifestPath, manifestJson, new UTF8Encoding(false));

            return manifest;
        }
        finally
        {
            if (Directory.Exists(hlslTempDir))
            {
                Directory.Delete(hlslTempDir, recursive: true);
            }
        }
    }

    private static string GetHlslRegister(int binding)
    {
        (char letter, int shift) = binding switch
        {
            < TShift => ('b', BShift),
            < SShift => ('t', TShift),
            < UShift => ('s', SShift),
            _ => ('u', UShift),
        };
        return $"{letter}{binding - shift}";
    }

    private static string GetDxcVersion(string dxcPath)
    {
        (int code, string output) = RunProcess(dxcPath, ["--version"]);
        if (code != 0)
        {
            throw new InvalidOperationException($"No se pudo obtener la versión de DXC ({dxcPath}): {output}");
        }

        return output.Trim();
    }

    private static (int Code, string Output) RunProcess(string exe, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardErrorEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
        };

        foreach (string arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"No se pudo iniciar el proceso {exe}.");

        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return (process.ExitCode, (stdout + stderr).Trim());
    }

    private static string FindRepoRoot()
    {
        string? current = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "Rodaje.slnx")))
            {
                return current;
            }

            current = Path.GetDirectoryName(current);
        }

        current = Directory.GetCurrentDirectory();
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "Rodaje.slnx")))
            {
                return current;
            }

            current = Path.GetDirectoryName(current);
        }

        throw new DirectoryNotFoundException("No se pudo localizar la raíz del repositorio de Rodaje (Rodaje.slnx).");
    }

    private static string FindDxc(string repoRoot)
    {
        string? env = Environment.GetEnvironmentVariable("RODAJE_DXC");
        if (!string.IsNullOrEmpty(env) && File.Exists(env))
        {
            return env;
        }

        string direct = Path.Combine(AppContext.BaseDirectory, "dxc.exe");
        if (File.Exists(direct))
        {
            return direct;
        }

        string sub = Path.Combine(AppContext.BaseDirectory, "dxc", "dxc.exe");
        if (File.Exists(sub))
        {
            return sub;
        }

        string nugetCache = Path.Combine(repoRoot, ".entorno", "nuget", "microsoft.direct3d.dxc", "1.9.2609.5", "build", "native", "bin", "x64", "dxc.exe");
        if (File.Exists(nugetCache))
        {
            return nugetCache;
        }

        throw new FileNotFoundException("No se encontró dxc.exe ni junto al ejecutable ni en la caché local de NuGet.");
    }
}
